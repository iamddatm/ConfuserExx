using System;
using System.Text.Json;

namespace SystemTextJsonRenaming {
	// System.Text.Json 按成员名匹配,而这些 DTO 没有 [JsonPropertyName] 把契约钉死,
	// 属性一旦被改名就会静默按默认值往返。
	public class Payload {
		public string DeploymentKey { get; set; } = "";
		public int Count { get; set; }
	}

	// 位置记录(Positional record)在反序列化时还会按名字绑定构造参数。
	public record Ticket(string OrderNumber, int Amount);

	// 泛型信封:容器自身的成员(Code)与被包裹的实参都是契约,而 T 是泛型形参,
	// 分析时没有对应的类型定义,必须能安全跳过而不是崩掉。
	public class Envelope<T> {
		public int Code { get; set; }
		public T Data { get; set; }
	}

	class Program {
		static Envelope<T> DecodeEnvelope<T>(string json) => JsonSerializer.Deserialize<Envelope<T>>(json);

		static int Main(string[] args) {
			Console.WriteLine("START");
			Console.WriteLine(JsonSerializer.Serialize(new Payload { DeploymentKey = "abc", Count = 7 }));
			if (Array.IndexOf(args, "--record") >= 0) {
				Console.WriteLine(JsonSerializer.Serialize(new Ticket("SN-1", 5)));
				var ticket = JsonSerializer.Deserialize<Ticket>("{\"OrderNumber\":\"SN-2\",\"Amount\":3}");
				Console.WriteLine(ticket.OrderNumber + "/" + ticket.Amount);
				var envelope = DecodeEnvelope<Ticket>("{\"Code\":3,\"Data\":{\"OrderNumber\":\"SN-3\",\"Amount\":2}}");
				Console.WriteLine(envelope.Code + "/" + envelope.Data.OrderNumber);
			}
			Console.WriteLine("END");
			return 42;
		}
	}
}