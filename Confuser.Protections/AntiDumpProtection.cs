using System;
using System.Collections.Generic;
using System.Linq;
using Confuser.Core;
using Confuser.Core.Helpers;
using Confuser.Core.Services;
using Confuser.Renamer;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Confuser.Protections {
	[BeforeProtection("Ki.ControlFlow")]
	internal class AntiDumpProtection : Protection {
		public const string _Id = "anti dump";
		public const string _FullId = "Ki.AntiDump";

		public override string Name {
			get { return "Anti Dump Protection"; }
		}

		public override string Description {
			get { return "This protection prevents the assembly from being dumped from memory."; }
		}

		public override string Id {
			get { return _Id; }
		}

		public override string FullId {
			get { return _FullId; }
		}

		public override ProtectionPreset Preset {
			get { return ProtectionPreset.Maximum; }
		}

		protected override void Initialize(ConfuserContext context) {
			//
		}

		protected override void PopulatePipeline(ProtectionPipeline pipeline) {
			pipeline.InsertPreStage(PipelineStage.ProcessModule, new AntiDumpPhase(this));
			pipeline.InsertPreStage(PipelineStage.EndModule, new ImageLayoutPhase(this));
		}

		class AntiDumpPhase : ProtectionPhase {
			public AntiDumpPhase(AntiDumpProtection parent)
				: base(parent) { }

			public override ProtectionTargets Targets {
				get { return ProtectionTargets.Modules; }
			}

			public override string Name {
				get { return "Anti-dump injection"; }
			}

			protected override void Execute(ConfuserContext context, ProtectionParameters parameters) {
				TypeDef rtType = context.Registry.GetService<IRuntimeService>().GetRuntimeType("Confuser.Runtime.AntiDump");

				var marker = context.Registry.GetService<IMarkerService>();
				var name = context.Registry.GetService<INameService>();

				foreach (ModuleDef module in parameters.Targets.OfType<ModuleDef>()) {
					IEnumerable<IDnlibDef> members = InjectHelper.Inject(rtType, module.GlobalType, module);

					MethodDef cctor = module.GlobalType.FindStaticConstructor();
					var init = (MethodDef)members.Single(method => method.Name == "Initialize");
					cctor.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, init));

					foreach (IDnlibDef member in members)
						name.MarkHelper(member, marker, (Protection)Parent);
				}
			}
		}

		/// <summary>
		///     给目标程序集补一个可写节，让运行时在单文件发布下也能拿到可写的内存映像。
		/// </summary>
		/// <remarks>
		///     单文件发布时 CoreCLR 对 bundle 内的程序集默认只做<b>只读</b>的文件映射（节按文件偏移排布），
		///     擦除 PE 头/元数据的写入会打在只读页上直接崩；但只要映像中存在任意一个可写节，
		///     CoreCLR 就会改用自己映射/拷贝出来的可写私有映像（节按虚拟地址排布），原地擦除才有落脚点。
		///     anti tamper 的密文节本来就是可写节，所以它单独开时不受影响；anti dump 单独开时就必须自己补一个。
		///     节名留空：构建期与运行期的 anti tamper 都按"节名前 8 字节算出的哈希是否为 0"决定跳过节内容哈希，
		///     空名会被两边一致跳过，因此与 anti tamper 同时开启也不会干扰它的密钥推导。
		/// </remarks>
		class ImageLayoutPhase : ProtectionPhase {
			public ImageLayoutPhase(AntiDumpProtection parent)
				: base(parent) { }

			public override ProtectionTargets Targets {
				get { return ProtectionTargets.Modules; }
			}

			public override string Name {
				get { return "Anti-dump image layout preparation"; }
			}

			protected override void Execute(ConfuserContext context, ProtectionParameters parameters) {
				if (!parameters.Targets.OfType<ModuleDef>().Any())
					return;
				// 非纯 IL 的模块本来就按混合模式写、不会被 bundle 的扁平加载路径命中，无需补节。
				if (!context.CurrentModule.IsILOnly)
					return;
				context.CurrentModuleWriterOptions.WriterEvent += WriterEvent;
			}

			void WriterEvent(object sender, ModuleWriterEventArgs e) {
				if (e.Event != ModuleWriterEvent.MDEndCreateTables)
					return;
				var section = new PESection("", 0xC0000040); //READ | WRITE | CNT_INITIALIZED_DATA
				section.Add(new ByteArrayChunk(new byte[4]), 4);
				e.Writer.Sections.Insert(0, section);
			}
		}
	}
}
