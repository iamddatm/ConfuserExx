using System;
using System.Threading.Tasks;
using Confuser.Core;
using Confuser.Core.Project;
using Confuser.UnitTest;
using Xunit;
using Xunit.Abstractions;

namespace SystemTextJsonRenaming.Test {
	public class SystemTextJsonRenamingTest : TestBase {
		public SystemTextJsonRenamingTest(ITestOutputHelper outputHelper) : base(outputHelper) { }

		const string App = "422_SystemTextJsonRenaming.dll";

		// renProperties=false 必须保留 JSON 契约所依赖的属性名。
		[Fact]
		[Trait("Category", "Protection")]
		[Trait("Protection", "rename")]
		public Task PropertiesKeepTheirNames() =>
			Run(App, null,
				new SettingItem<Protection>("rename") { ["renProperties"] = "false" },
				outputDirSuffix: "-stj-properties",
				checkOutput: false,
				postProcessAction: outputPath => {
					AssertAppOutput(outputPath, null, new[] {
						"START",
						"{\"DeploymentKey\":\"abc\",\"Count\":7}",
						"END"
					});
					return Task.CompletedTask;
				});

		// 位置记录还会按名字绑定构造参数,所以要 renProperties=false 与 renameArgs=false 一起用,
		// 往返才能存活。
		[Fact]
		[Trait("Category", "Protection")]
		[Trait("Protection", "rename")]
		public Task RecordsRoundTripWhenParameterNamesAreKept() =>
			Run(App, null,
				new SettingItem<Protection>("rename") { ["renProperties"] = "false", ["renameArgs"] = "false" },
				outputDirSuffix: "-stj-record",
				checkOutput: false,
				postProcessAction: outputPath => {
					AssertAppOutput(outputPath, "--record", new[] {
						"START",
						"{\"DeploymentKey\":\"abc\",\"Count\":7}",
						"{\"OrderNumber\":\"SN-1\",\"Amount\":5}",
						"SN-2/3",
						"3/SN-3",
						"END"
					});
					return Task.CompletedTask;
				});

		// 自动检测:能从 JsonSerializer 调用点认出被序列化的类型,连参数都不用配。
		[Fact]
		[Trait("Category", "Protection")]
		[Trait("Protection", "rename")]
		public Task AutoDetectionKeepsSerializedMembers() =>
			Run(App, null,
				new SettingItem<Protection>("rename"),
				outputDirSuffix: "-stj-auto",
				checkOutput: false,
				postProcessAction: outputPath => {
					AssertAppOutput(outputPath, "--record", new[] {
						"START",
						"{\"DeploymentKey\":\"abc\",\"Count\":7}",
						"{\"OrderNumber\":\"SN-1\",\"Amount\":5}",
						"SN-2/3",
						"3/SN-3",
						"END"
					});
					return Task.CompletedTask;
				});

		static void AssertAppOutput(string outputPath, string arguments, string[] expected) {
			var (exitCode, stdout, stderr) = RunApp(outputPath, App, arguments);
			Assert.Equal(42, exitCode);
			Assert.Empty(stderr);
			Assert.Equal(expected, stdout.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries));
		}
	}
}