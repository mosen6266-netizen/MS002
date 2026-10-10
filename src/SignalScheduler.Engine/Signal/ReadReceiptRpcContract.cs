using System.Text.Json;

namespace SignalScheduler.Engine.Signal;

public enum ReceiptRpcStatus { Accepted, Rejected, Uncertain }

public sealed record ReceiptRpcCheck(ReceiptRpcStatus Status,string Code);

/// <summary>
/// signal-cli 0.14.9 sendReceipt contract, without message contents or recipients
/// in diagnostic output. Accepted means RPC accepted, not remote UI marked read.
/// </summary>
public static class ReadReceiptRpcContract
{
    public static object Request(string account,string recipient,long[] timestamps,string id)=>
        new {
            jsonrpc="2.0",method="sendReceipt",id,
            @params=new {account,recipient,targetTimestamps=timestamps,type="read"}
        };

    public static bool IsSafeCode(string? code)=>code is
        "READ_DISABLED" or "NO_PENDING_FOR_SPEAKER" or
        "RPC_NOT_STARTED" or "RPC_NOT_CONFIRMED" or "RPC_ACCEPTED" or
        "RPC_EMPTY_RESPONSE" or "RPC_MISMATCH" or "RPC_INTERNAL_ERROR" or
        "RPC_ERROR_OTHER" or "RPC_HTTP_ERROR" or "RPC_NO_RESULT" or
        "RPC_INVALID_JSON" or "RPC_TIMEOUT" or "RPC_NETWORK_ERROR" or
        "RPC_RESPONSE_ERROR" or "RPC_UNCLASSIFIED" or "RPC_INTERRUPTED" or
        "RPC_REJECTED_-32600" or "RPC_REJECTED_-32601" or "RPC_REJECTED_-32602";

    public static ReceiptRpcCheck Check(int httpStatus,string? body,string id)
    {
        if(string.IsNullOrWhiteSpace(body))
            return new(ReceiptRpcStatus.Uncertain,"RPC_EMPTY_RESPONSE");
        try
        {
            using var doc=JsonDocument.Parse(body);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !root.TryGetProperty("jsonrpc",out var version) ||
               version.ValueKind!=JsonValueKind.String ||
               version.GetString()!="2.0" ||
               !root.TryGetProperty("id",out var rpcId) ||
               rpcId.ValueKind!=JsonValueKind.String || rpcId.GetString()!=id)
                return new(ReceiptRpcStatus.Uncertain,"RPC_MISMATCH");
            if(root.TryGetProperty("error",out var error) &&
               error.ValueKind!=JsonValueKind.Null)
            {
                if(error.ValueKind==JsonValueKind.Object &&
                   error.TryGetProperty("code",out var code) &&
                   code.ValueKind==JsonValueKind.Number &&
                   code.TryGetInt32(out var number))
                {
                    if(number is -32600 or -32601 or -32602)
                        return new(ReceiptRpcStatus.Rejected,$"RPC_REJECTED_{number}");
                    if(number==-32603)
                        return new(ReceiptRpcStatus.Uncertain,"RPC_INTERNAL_ERROR");
                }
                return new(ReceiptRpcStatus.Uncertain,"RPC_ERROR_OTHER");
            }
            if(httpStatus<200 || httpStatus>=300)
                return new(ReceiptRpcStatus.Uncertain,"RPC_HTTP_ERROR");
            return root.TryGetProperty("result",out _)
                ?new(ReceiptRpcStatus.Accepted,"RPC_ACCEPTED")
                :new(ReceiptRpcStatus.Uncertain,"RPC_NO_RESULT");
        }
        catch(JsonException)
        {
            return new(ReceiptRpcStatus.Uncertain,"RPC_INVALID_JSON");
        }
    }
}
