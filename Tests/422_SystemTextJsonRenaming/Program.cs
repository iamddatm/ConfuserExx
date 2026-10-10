using System;
using System.Text.Json;
using System.Threading.Tasks;

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

	// 只经"泛型帮助方法"链路才被序列化的类型:调用点上只有 DecodeEnvelope<DeepNested> 是具体类型,
	// 真正的 JsonSerializer 调用藏在两层之外,泛型实参在那里只是形参 T。
	public class DeepNested {
		public string OnlyViaGenericChain { get; set; } = "";
	}

	// 只经 async 泛型帮助方法链路才被序列化的类型:C# 会把 async 方法体搬进编译器生成的状态机,
// 泛型形参随之变成状态机的类型形参,分析器得靠 [AsyncStateMachine] 把 MoveNext 归还原方法。
	public class AsyncNested {
		public string OnlyViaAsyncChain { get; set; } = "";
	}

	class Program {
		static Envelope<T> ReadPayload<T>(string json) => JsonSerializer.Deserialize<Envelope<T>>(json);
		static Envelope<T> DecodeEnvelope<T>(string json) => ReadPayload<T>(json);

		static async Task<Envelope<T>> ReadPayloadAsync<T>(string json) {
			await Task.Yield();
			return JsonSerializer.Deserialize<Envelope<T>>(json);
		}

		static async Task<T> DecodeAsync<T>(string json) => (await ReadPayloadAsync<T>(json)).Data;

		// 匿名类型的构造参数名就是它的属性名:一旦被置空,STJ 连构建类型信息都会抛
		// ("parameters with null names"),请求体因此发不出去。
		static string SerializeAnonymous(string instanceId) => JsonSerializer.Serialize(new { InstanceId = instanceId });

		static int Main(string[] args) {
			Console.WriteLine("START");
			Console.WriteLine(JsonSerializer.Serialize(new Payload { DeploymentKey = "abc", Count = 7 }));
			if (Array.IndexOf(args, "--record") >= 0) {
				Console.WriteLine(JsonSerializer.Serialize(new Ticket("SN-1", 5)));
				var ticket = JsonSerializer.Deserialize<Ticket>("{\"OrderNumber\":\"SN-2\",\"Amount\":3}");
				Console.WriteLine(ticket.OrderNumber + "/" + ticket.Amount);
				var envelope = DecodeEnvelope<Ticket>("{\"Code\":3,\"Data\":{\"OrderNumber\":\"SN-3\",\"Amount\":2}}");
				Console.WriteLine(envelope.Code + "/" + envelope.Data.OrderNumber);
				var deep = DecodeEnvelope<DeepNested>("{\"Code\":4,\"Data\":{\"OnlyViaGenericChain\":\"chained\"}}");
				Console.WriteLine(deep.Data.OnlyViaGenericChain);
				var asyncDeep = DecodeAsync<AsyncNested>("{\"Code\":5,\"Data\":{\"OnlyViaAsyncChain\":\"async-chained\"}}").GetAwaiter().GetResult();
				Console.WriteLine(asyncDeep.OnlyViaAsyncChain);
				Console.WriteLine(SerializeAnonymous("anon"));
			}
			Console.WriteLine("END");
			return 42;
		}
	}
}