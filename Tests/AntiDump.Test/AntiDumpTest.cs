using System.Threading.Tasks;
using Confuser.Core;
using Confuser.Core.Project;
using Confuser.UnitTest;
using Xunit;
using Xunit.Abstractions;

namespace AntiDump.Test {
	public sealed class AntiDumpTest : TestBase {
		public AntiDumpTest(ITestOutputHelper outputHelper) : base(outputHelper) { }

		[Fact]
		[Trait("Category", "Protection")]
		[Trait("Protection", "anti dump")]
		public Task ProtectAntiDumpAndExecute() {
			return Run("AntiTamper.exe",
				new[] { "This is a test." },
				new SettingItem<Protection>("anti dump"),
				"_antidump");
		}
	}
}
