using ComplianceMonitor.Client;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
return await new ConsoleApplication(http, Console.In, Console.Out, Console.Error).RunAsync(args, cancellation.Token);
