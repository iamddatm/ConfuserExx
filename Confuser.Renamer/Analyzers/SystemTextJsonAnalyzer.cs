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
	/// 这里从调用点反推:扫方法体里对 System.Text.Json.JsonSerializer 的调用,取出泛型实参,
	/// 把该类型的属性/字段与构造参数标记为不可改名。属性走 <c>SetCanRename(false)</c>(分析阶段
	/// 即时生效),构造参数走 <c>SetParam(..., "renameArgs", "false")</c>(重命名阶段才读取,
	/// 与 JsonAnalyzer 一致)。
	///
	/// 已知边界:非泛型重载(如 <c>Deserialize(string, Type, ...)</c>)、反射与源生成器路径不在
	/// 覆盖范围内,这类场景仍需手工设 renProperties=false。字段只在显式开启 IncludeFields 时才是
	/// 契约,这里按"宁可少改名"处理,一并保留。
	/// </summary>
	internal sealed class SystemTextJsonAnalyzer : IRenamer {
		const string JsonSerializerType = "System.Text.Json.JsonSerializer";

		public void Analyze(ConfuserContext context, INameService service, ProtectionParameters parameters, IDnlibDef def) {
			if (!(def is MethodDef method) || !method.HasBody)
				return;

			// 同一个类型可能被多处调用点命中,避免重复遍历
			var visited = new HashSet<TypeDef>();

			foreach (var instruction in method.Body.Instructions) {
				if (instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt)
					continue;
				// 泛型调用点(MethodSpec)不实现 IMethodDefOrRef,必须按 IMethod 取
				if (!(instruction.Operand is IMethod target))
					continue;
				if (target.DeclaringType == null || target.DeclaringType.FullName != JsonSerializerType)
					continue;

				// 泛型实参才是被序列化的类型:JsonSerializer.Serialize<TValue>(...) / Deserialize<TValue>(...)
				if (!(target is MethodSpec spec) || spec.GenericInstMethodSig == null)
					continue;

				foreach (var typeArgument in spec.GenericInstMethodSig.GenericArguments)
					ExcludeMembers(context, service, typeArgument, visited);
			}
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