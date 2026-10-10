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

	class Program {
		static int Main(string[] args) {
			Console.WriteLine("START");
			Console.WriteLine(JsonSerializer.Serialize(new Payload { DeploymentKey = "abc", Count = 7 }));
			if (Array.IndexOf(args, "--record") >= 0) {
				Console.WriteLine(JsonSerializer.Serialize(new Ticket("SN-1", 5)));
				var ticket = JsonSerializer.Deserialize<Ticket>("{\"OrderNumber\":\"SN-2\",\"Amount\":3}");
				Console.WriteLine(ticket.OrderNumber + "/" + ticket.Amount);
			}
			Console.WriteLine("END");
			return 42;
		}
	}
}