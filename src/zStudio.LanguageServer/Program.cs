using StreamJsonRpc;
using zStudio.LanguageServer;

using var server = new GameZLanguageServer();
using var handler = new HeaderDelimitedMessageHandler(Console.OpenStandardOutput(), Console.OpenStandardInput(), new SystemTextJsonFormatter());
using var rpc = new JsonRpc(handler, server);
server.Rpc = rpc;
rpc.StartListening();
try { await rpc.Completion; }
catch (Exception ex) when (ex is IOException or OperationCanceledException) { await Console.Error.WriteLineAsync(ex.Message); }
