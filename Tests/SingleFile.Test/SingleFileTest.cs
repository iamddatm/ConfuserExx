using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace SingleFile.Test {
	/// <summary>
	///     端到端跑"混淆 → 单文件发布 → 执行"链路，锁住 anti dump / anti tamper 在
	///     <c>PublishSingleFile=true</c> 下能正常工作（它们在单文件下的内存布局与磁盘加载不同，
	///     历史上会分别以 ACCESS_VIOLATION / CLR 异常崩掉）。
	/// </summary>
	public sealed class SingleFileTest {
		readonly ITestOutputHelper output;

		public SingleFileTest(ITestOutputHelper outputHelper) {
			output = outputHelper;
		}

		static string Metadata(string key) {
			return Assembly.GetExecutingAssembly()
				.GetCustomAttributes<AssemblyMetadataAttribute>()
				.FirstOrDefault(a => a.Key == key)?.Value;
		}

		[Theory]
		[InlineData("anti dump")]
		[InlineData("anti tamper")]
		[InlineData("anti dump|anti tamper")]
		[Trait("Category", "SingleFile")]
		public void ObfuscatedAssemblyStillRunsAsSingleFile(string protections) {
			string sampleProject = Metadata("SingleFileSampleProject");
			string cliPath = Metadata("ConfuserCliPath");
			string configuration = Metadata("BuildConfiguration");
			Assert.True(File.Exists(sampleProject), "样本工程不存在: " + sampleProject);
			Assert.True(File.Exists(cliPath), "ConfuserExx CLI 不存在（应先构建 Confuser.CLI）: " + cliPath);

			string workDir = Path.Combine(Path.GetTempPath(), "confuser-singlefile-" + Guid.NewGuid().ToString("N"));
			string outputDir = Path.Combine(workDir, "publish");
			string plainDir = Path.Combine(workDir, "plain");
			Directory.CreateDirectory(workDir);
			try {
				string projectFile = Path.Combine(workDir, "case.crproj");
				File.WriteAllText(projectFile, BuildProjectFile(protections));

				string publishLog = Publish(sampleProject, configuration, workDir, outputDir, cliPath, projectFile, obfuscate: true);
				output.WriteLine(publishLog);
				// ConfuserExx 跑过才会打印这条；同时样本工程里的哨兵会保证钩子失配时 publish 失败
				Assert.Contains("[INFO] Done.", publishLog);

				string exePath = Path.Combine(outputDir, "SingleFileSample.exe");
				Assert.True(File.Exists(exePath), "单文件产物不存在: " + exePath);

				// 再出一份未混淆的做对照：单文件 bundle 里存的是程序集原始字节，
				// 混淆 dll 明显更大（注入运行时桩 + 密文节），因此 exe 也应当更大 ——
				// 用它确认"打进单文件里的确实是混淆产物"，而不是被重新拷贝覆盖回未混淆的 dll。
				Publish(sampleProject, configuration, workDir, plainDir, cliPath, projectFile, obfuscate: false);
				long obfuscatedSize = new FileInfo(exePath).Length;
				long plainSize = new FileInfo(Path.Combine(plainDir, "SingleFileSample.exe")).Length;
				Assert.True(obfuscatedSize > plainSize,
					string.Format("混淆后的单文件 exe 应当更大：混淆 {0} vs 未混淆 {1}", obfuscatedSize, plainSize));

				string runLog = RunToCompletion(exePath, "", outputDir, out int exitCode);
				output.WriteLine(runLog);
				Assert.Contains("START", runLog);
				Assert.Contains("Hello from single-file", runLog);
				Assert.Contains("END", runLog);
				Assert.Equal(42, exitCode);
			}
			finally {
				try { Directory.Delete(workDir, true); } catch { }
			}
		}

		static string Publish(string sampleProject, string configuration, string workingDirectory,
			string outputDir, string cliPath, string projectFile, bool obfuscate) {
			return RunToCompletion("dotnet", string.Join(" ", new[] {
				"publish", Quote(sampleProject),
				"-c", configuration,
				"-r", "win-x64",
				"-p:SelfContained=true",
				"-p:PublishSingleFile=true",
				"-p:Obfuscate=" + (obfuscate ? "true" : "false"),
				"-p:ConfuserCliPath=" + Quote(cliPath),
				"-p:ConfuserProjectPath=" + Quote(projectFile),
				"-o", Quote(outputDir)
			}), workingDirectory);
		}

		static string BuildProjectFile(string protections) {
			string rules = string.Concat(protections
				.Split('|')
				.Select(p => "    <protection id=\"" + p + "\" />\n"));
			return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
				"<project baseDir=\".\" outputDir=\".\" seed=\"20261010\" xmlns=\"http://confuser.codeplex.com\">\n" +
				"  <rule pattern=\"true\" preset=\"none\" inherit=\"false\">\n" +
				rules +
				"  </rule>\n" +
				"  <module path=\"SingleFileSample.dll\" />\n" +
				"</project>\n";
		}

		static string Quote(string value) => "\"" + value + "\"";

		static string RunToCompletion(string fileName, string arguments, string workingDirectory) {
			return RunToCompletion(fileName, arguments, workingDirectory, out _);
		}

		static string RunToCompletion(string fileName, string arguments, string workingDirectory, out int exitCode) {
			var startInfo = new ProcessStartInfo(fileName, arguments) {
				WorkingDirectory = workingDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using (var process = Process.Start(startInfo)) {
				string stdout = process.StandardOutput.ReadToEnd();
				string stderr = process.StandardError.ReadToEnd();
				process.WaitForExit();
				exitCode = process.ExitCode;
				return stdout + stderr;
			}
		}
	}
}
