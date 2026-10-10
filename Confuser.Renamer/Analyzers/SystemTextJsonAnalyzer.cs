using System.Collections.Generic;
using Confuser.Core;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Confuser.Renamer.Analyzers {
	/// <summary>
	/// System.Text.Json 按成员名(以及位置记录的构造参数名)绑定契约,而这个契约**没有**任何
	/// 特性可以把它钉死——不带 [JsonPropertyName] 时,属性一改名,序列化/反序列化就静默读写默认值。
	/// 因此无法像 Newtonsoft.Json 那样只看特性(见 <see cref="JsonAnalyzer"/>)。
	///
	/// 从调用点反推被序列化的类型:
	/// 1. 直接命中——方法体里对 JsonSerializer 的调用,取其泛型实参(含数组与泛型容器,自定义泛型
	///    DTO 的自身成员同样是契约,如 ApiResponse&lt;T&gt; 的 Code/Data);
	/// 2. 泛型帮助方法链路——实参在真正的 JsonSerializer 调用点只是形参 T,必须沿着调用图把具体
	///    实参代回去。例如 SendApiAsync&lt;T&gt; → ReadApiResponseAsync&lt;T&gt; → Deserialize&lt;ApiResponse&lt;T&gt;&gt;,
	///    调用点 GetConfig() → SendApiAsync&lt;WorkerServerConfig&gt; 里的 WorkerServerConfig 才是会被序列化的类型。
	/// 3. 异步方法——C# 会把 async 方法的体搬进编译器生成的 &lt;&gt;d__N 状态机,泛型形参也变成状态机
	///    自己的类型形参;这时必须靠 [AsyncStateMachine] 把 MoveNext 归还原方法,否则整条链路都断在
	///    "T 不属于本方法"上(异步 + 泛型 + JSON 的组合非常常见)。
	///
	/// 命中的类型:属性/字段走 <c>SetCanRename(false)</c>(分析阶段即时生效),构造参数走
	/// <c>SetParam(..., "renameArgs", "false")</c>(重命名阶段才读取,与 JsonAnalyzer 一致)。
	/// 判定"实参是否来自本方法自己的泛型形参"时只要求"签名里含泛型形参且原方法是泛型方法",
	/// 不追 Owner:宁可贵一点改名覆盖,也不要把该保护的 DTO 漏掉。
	///
	/// 已知边界:非泛型重载(如 <c>Deserialize(string, Type, ...)</c>)、反射与源生成器路径不覆盖,
	/// 这类场景仍需手工设 renProperties=false。字段只在显式开启 IncludeFields 时才是契约,
	/// 这里按"宁可少改名"处理,一并保留。
	/// </summary>
	internal sealed class SystemTextJsonAnalyzer : IRenamer {
		const string JsonSerializerType = "System.Text.Json.JsonSerializer";
		const string AsyncStateMachineAttribute = "System.Runtime.CompilerServices.AsyncStateMachineAttribute";

		bool scanned;

		public void Analyze(ConfuserContext context, INameService service, ProtectionParameters parameters, IDnlibDef def) {
			if (!(def is MethodDef method) || !method.HasBody)
				return;
			if (scanned)
				return;
			// 调用图分析要看全部方法体,不适合按单个 def 增量做,首次进来整体扫一遍即可
			scanned = true;
			MarkSerializedTypes(context, service);
		}

		void MarkSerializedTypes(ConfuserContext context, INameService service) {
			var methods = new List<MethodDef>();
			foreach (var module in context.Modules) {
				foreach (var type in module.GetTypes()) {
					foreach (var method in type.Methods) {
						if (method.HasBody)
							methods.Add(method);
					}
				}
			}

			var asyncOwners = BuildAsyncOwners(methods);
			var inScope = new HashSet<MethodDef>(methods);
			var visited = new HashSet<TypeDef>();
			// 会把"自己的泛型形参"喂给序列化器的方法(直接命中或经调用传播而来)
			var serializing = new HashSet<MethodDef>();
			// 反向调用边:被调方法 -> (调用者, 调用点上的泛型实参),调用者已归还原方法
			var callers = new Dictionary<MethodDef, List<(MethodDef Caller, IList<TypeSig> Arguments)>>();

			foreach (var method in methods) {
				var owner = OriginalOwner(method, asyncOwners);
				foreach (var instruction in method.Body.Instructions) {
					if (instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt)
						continue;
					// 泛型调用点(MethodSpec)不实现 IMethodDefOrRef,必须按 IMethod 取
					if (!(instruction.Operand is IMethod target))
						continue;

					if (target.DeclaringType != null && target.DeclaringType.FullName == JsonSerializerType) {
						if (!(target is MethodSpec serializerCall) || serializerCall.GenericInstMethodSig == null)
							continue;
						var arguments = serializerCall.GenericInstMethodSig.GenericArguments;
						foreach (var argument in arguments)
							ExcludeMembers(context, service, argument, visited);
						if (owner.GenericParameters.Count > 0 && ContainsGenericParameter(arguments))
							serializing.Add(owner);
						continue;
					}

					if (!(target is MethodSpec call) || call.GenericInstMethodSig == null)
						continue;
					var callee = call.Method.ResolveMethodDef();
					if (callee == null || !inScope.Contains(callee))
						continue;
					if (!callers.TryGetValue(callee, out var callSites)) {
						callSites = new List<(MethodDef, IList<TypeSig>)>();
						callers[callee] = callSites;
					}
					callSites.Add((owner, call.GenericInstMethodSig.GenericArguments));
				}
			}

			// 传递闭包:只有当调用点上喂过去的实参涉及调用者自己的泛型形参时,才继续往上传播,
			// 免得把"顺手序列化了个无关具体类型"的方法也算进来
			var pending = new Queue<MethodDef>(serializing);
			while (pending.Count > 0) {
				var callee = pending.Dequeue();
				if (!callers.TryGetValue(callee, out var callSites))
					continue;
				foreach (var (caller, arguments) in callSites) {
					if (serializing.Contains(caller) || caller.GenericParameters.Count == 0 || !ContainsGenericParameter(arguments))
						continue;
					serializing.Add(caller);
					pending.Enqueue(caller);
				}
			}

			// 落地点:凡是"会把类型参数喂给序列化器"的方法,其每个调用点上的实参都是被序列化的类型
			foreach (var callee in serializing) {
				if (!callers.TryGetValue(callee, out var callSites))
					continue;
				foreach (var (_, arguments) in callSites) {
					foreach (var argument in arguments)
						ExcludeMembers(context, service, argument, visited);
				}
			}
		}

		/// <summary>编译器生成的 async 状态机类型 -&gt; 原方法(靠原方法上的 [AsyncStateMachine] 反查)。</summary>
		static Dictionary<TypeDef, MethodDef> BuildAsyncOwners(IEnumerable<MethodDef> methods) {
			var owners = new Dictionary<TypeDef, MethodDef>();
			foreach (var method in methods) {
				foreach (var attribute in method.CustomAttributes) {
					if (attribute.TypeFullName != AsyncStateMachineAttribute || !attribute.HasConstructorArguments)
						continue;
					var argument = attribute.ConstructorArguments[0].Value;
					var stateMachine = (argument as ITypeDefOrRef)?.ResolveTypeDef()
						?? (argument as TypeSig)?.ToTypeDefOrRef()?.ResolveTypeDef();
					if (stateMachine != null)
						owners[stateMachine] = method;
				}
			}
			return owners;
		}

		/// <summary>把状态机的 MoveNext 归还原 async 方法;其余原样返回。</summary>
		static MethodDef OriginalOwner(MethodDef method, Dictionary<TypeDef, MethodDef> asyncOwners) {
			if (method.Name == "MoveNext" && asyncOwners.TryGetValue(method.DeclaringType, out var owner))
				return owner;
			return method;
		}

		/// <summary>递归进入数组与泛型实参(List/Dictionary/Nullable 等),只把真正是 DTO 的类型挑出来。</summary>
		static void ExcludeMembers(ConfuserContext context, INameService service, TypeSig signature, HashSet<TypeDef> visited) {
			switch (signature) {
				case GenericInstSig genericInstance:
					// 自定义泛型 DTO(如 ApiResponse&lt;T&gt;)的成员本身就是契约,要连容器一起看;
					// BCL 容器(List&lt;T&gt; 等)会在下面的 IsInScope 处被挡掉。
					ExcludeMembers(context, service, genericInstance.GenericType, visited);
					foreach (var argument in genericInstance.GenericArguments)
						ExcludeMembers(context, service, argument, visited);
					return;
				case SZArraySig array:
					ExcludeMembers(context, service, array.Next, visited);
					return;
				case ArraySig array:
					ExcludeMembers(context, service, array.Next, visited);
					return;
			}

			// 泛型形参(方法/类型的 T)没有对应的类型定义:ToTypeDefOrRef() 返回 null,不能直接解引用
			var typeRef = signature.ToTypeDefOrRef();
			if (typeRef == null)
				return;
			var typeDef = typeRef.ResolveTypeDef();
			if (typeDef == null)
				return;
			// 只处理本次要混淆的模块里的类型,别去动 BCL 的成员
			if (!IsInScope(context, typeDef.Module))
				return;
			if (!visited.Add(typeDef))
				return;

			foreach (var property in typeDef.Properties)
				service.SetCanRename(property, false);
			foreach (var field in typeDef.Fields)
				service.SetCanRename(field, false);
			foreach (var method in typeDef.Methods) {
				if (method.IsConstructor)
					service.SetParam(method, "renameArgs", "false");
			}

			// 继承来的成员同样是契约的一部分
			if (typeDef.BaseType != null)
				ExcludeMembers(context, service, typeDef.BaseType.ToTypeSig(), visited);
		}

		static bool ContainsGenericParameter(IList<TypeSig> arguments) {
			foreach (var argument in arguments) {
				if (ContainsGenericParameter(argument))
					return true;
			}
			return false;
		}

		/// <summary>签名里是否出现了泛型形参(方法形参 !T 或类型形参 !T)。</summary>
		static bool ContainsGenericParameter(TypeSig signature) {
			switch (signature) {
				case GenericVar _:
				case GenericMVar _:
					return true;
				case GenericInstSig genericInstance:
					return ContainsGenericParameter(genericInstance.GenericArguments);
				case SZArraySig array:
					return ContainsGenericParameter(array.Next);
				case ArraySig array:
					return ContainsGenericParameter(array.Next);
			}
			return false;
		}

		static bool IsInScope(ConfuserContext context, ModuleDef module) {
			foreach (var candidate in context.Modules) {
				if (ReferenceEquals(candidate, module))
					return true;
			}
			return false;
		}

		public void PreRename(ConfuserContext context, INameService service, ProtectionParameters parameters, IDnlibDef def) {
			//
		}

		public void PostRename(ConfuserContext context, INameService service, ProtectionParameters parameters, IDnlibDef def) {
			//
		}
	}
}