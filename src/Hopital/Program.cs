using System.CommandLine;
using System.Net;
using Hopital.Generation;
using Hopital.Hl7;
using System.Text.Json;
using Hopital.Mllp;
using Hopital.Receiver;
using Hopital.Scenarios;
using Hopital.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss.fff ";
    }));

var root = new RootCommand("Hopital, a fake hospital: plays the Epic side of an HL7 integration over MLLP, with made-up data only.");
root.Subcommands.Add(RunCommand());
root.Subcommands.Add(ReceiveCommand());
root.Subcommands.Add(ModeCommand());
root.Subcommands.Add(SendCommand());
root.Subcommands.Add(ListenCommand());
root.Subcommands.Add(GenerateCommand());
root.Subcommands.Add(SamplesCommand());
return await root.Parse(args).InvokeAsync();

Command RunCommand()
{
    var file = new Argument<FileInfo>("scenario") { Description = "Scenario YAML file, e.g. scenarios/smoke-test.yaml." };
    var host = new Option<string>("--host") { Description = "Host to send to.", DefaultValueFactory = _ => "127.0.0.1" };
    var port = new Option<int>("--port") { Description = "MLLP port to send to.", DefaultValueFactory = _ => 6661 };
    var dryRun = new Option<bool>("--dry-run") { Description = "Print the messages instead of sending them (no waits)." };

    var command = new Command("run", "Run a scenario: send its messages in order and check each ACK.") { file, host, port, dryRun };
    command.SetAction(async (parse, cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("run");
        ScenarioPlan plan;
        try
        {
            plan = ScenarioPlanner.Build(ScenarioLoader.Load(parse.GetValue(file)!.FullName), TimeProvider.System);
        }
        catch (Exception ex) when (ex is ScenarioException or IOException)
        {
            logger.LogError("{Reason}", ex.Message);
            return 2;
        }

        if (parse.GetValue(dryRun))
        {
            foreach (var send in plan.Sends)
            {
                var message = send.Build();
                Console.WriteLine($"#{send.Number} {send.Label}  {message.ControlId}  after {send.DelayBefore.TotalSeconds:0.###} s, expect {send.Expect}");
                PrintSegments(message.Text);
            }
            return 0;
        }

        var target = $"{parse.GetValue(host)}:{parse.GetValue(port)}";
        var total = plan.Sends.Sum(s => s.DelayBefore.TotalSeconds);
        logger.LogInformation("Running {Name} ({Count} messages, about {Seconds:0} s of waits) against {Target}", plan.Name, plan.Sends.Count, total, target);
        var runner = new ScenarioRunner(parse.GetValue(host)!, parse.GetValue(port), logger, TimeProvider.System);
        var summary = await runner.RunAsync(plan, cancellationToken);
        foreach (var line in summary.Describe())
            Console.WriteLine(line);
        return summary.Failed == 0 && !summary.Stopped ? 0 : 1;
    });
    return command;
}

Command ReceiveCommand()
{
    var port = new Option<int>("--port") { Description = "MLLP port results are sent to.", DefaultValueFactory = _ => 6662 };
    var bind = new Option<string>("--bind") { Description = "Address to listen on (0.0.0.0 = all).", DefaultValueFactory = _ => "0.0.0.0" };
    var output = new Option<DirectoryInfo>("--out") { Description = "Folder to save received messages in.", DefaultValueFactory = _ => new DirectoryInfo("received") };
    var mode = new Option<ReceiverMode>("--mode") { Description = "Starting mode: accept, error, reject, hang, drop or offline.", DefaultValueFactory = _ => ReceiverMode.Accept };
    var controlPort = new Option<int>("--control-port") { Description = "HTTP port for switching modes remotely (0 = off).", DefaultValueFactory = _ => 6680 };
    var scenarios = new Option<DirectoryInfo>("--scenarios")
    {
        Description = "Folder of scenarios the web page can run (default: ./scenarios, else the one next to the program).",
        DefaultValueFactory = _ => new DirectoryInfo(Directory.Exists("scenarios") ? "scenarios" : Path.Combine(AppContext.BaseDirectory, "scenarios")),
    };

    var command = new Command("receive", "Play Epic receiving results: save every message and answer AA, AE, AR or not at all.")
    {
        port, bind, output, mode, controlPort, scenarios,
    };
    command.SetAction(async (parse, cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("receive");
        var clock = TimeProvider.System;
        var state = new ReceiverState(clock);
        state.Set(parse.GetValue(mode));
        state.Changed += s => logger.LogWarning("Mode is now {Mode}{Detail}", s.Mode, DescribeTemporary(s));
        var store = new MessageStore(parse.GetValue(output)!.FullName, clock);
        logger.LogInformation("Saving received messages in {Root}", store.Root);
        var hub = new EventHub();
        await using var runs = new RunManager(parse.GetValue(scenarios)!.FullName, hub, loggerFactory.CreateLogger("web-run"), clock);

        WebApplication? control = null;
        if (parse.GetValue(controlPort) is > 0 and var httpPort)
        {
            control = ControlApi.Build(parse.GetValue(bind)!, httpPort, state, store, runs, hub);
            await control.StartAsync(cancellationToken);
            logger.LogInformation("Web page and API at http://{Bind}:{Port}/ (scenarios from {Scenarios})", parse.GetValue(bind), httpPort, runs.Folder);
        }

        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("Keys: [a]ccept [e]rror [r]eject [h]ang [d]rop [o]ffline [s]tatus. Ctrl+C to stop.");
            _ = Task.Run(() =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var key = char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
                    ReceiverMode? chosen = key switch
                    {
                        'a' => ReceiverMode.Accept,
                        'e' => ReceiverMode.Error,
                        'r' => ReceiverMode.Reject,
                        'h' => ReceiverMode.Hang,
                        'd' => ReceiverMode.Drop,
                        'o' => ReceiverMode.Offline,
                        _ => null,
                    };
                    if (chosen is { } m)
                        state.Set(m);
                    else
                    {
                        var s = state.Status;
                        Console.WriteLine($"Mode {s.Mode}{DescribeTemporary(s)}, {s.Received} received. Keys: a e r h d o s.");
                    }
                }
            }, cancellationToken);
        }

        try
        {
            var receiver = new FakeEpicReceiver(IPAddress.Parse(parse.GetValue(bind)!), parse.GetValue(port), state, store, logger);
            receiver.MessageSaved += saved => hub.Publish("received", saved);
            await receiver.RunAsync(cancellationToken);
        }
        finally
        {
            if (control is not null)
            {
                await control.StopAsync(CancellationToken.None);
                await control.DisposeAsync();
            }
        }
        return 0;
    });
    return command;
}

Command ModeCommand()
{
    var mode = new Argument<string>("mode") { Description = "accept, error, reject, hang, drop, offline, or status to just show it." };
    var host = new Option<string>("--host") { Description = "Where the receiver runs.", DefaultValueFactory = _ => "127.0.0.1" };
    var controlPort = new Option<int>("--control-port") { Description = "The receiver's HTTP control port.", DefaultValueFactory = _ => 6680 };
    var messages = new Option<int?>("--messages") { Description = "Go back to accept after this many messages." };
    var duration = new Option<string?>("--for") { Description = "Go back to accept after this long, e.g. 30s or 2m." };
    var text = new Option<string?>("--text") { Description = "AE/AR text for MSA-3 and ERR." };

    var command = new Command("mode", "Switch a running receiver's mode, or show its status.") { mode, host, controlPort, messages, duration, text };
    command.SetAction(async (parse, cancellationToken) =>
    {
        var target = $"http://{parse.GetValue(host)}:{parse.GetValue(controlPort)}/";
        using var http = new HttpClient { BaseAddress = new Uri(target), Timeout = TimeSpan.FromSeconds(10) };
        var name = parse.GetValue(mode)!.Trim().ToLowerInvariant();
        try
        {
            HttpResponseMessage response;
            if (name == "status")
                response = await http.GetAsync("status", cancellationToken);
            else
            {
                var query = new List<string>();
                if (parse.GetValue(messages) is { } n)
                    query.Add($"messages={n}");
                if (parse.GetValue(duration) is { } d)
                    query.Add($"for={Uri.EscapeDataString(d)}");
                if (parse.GetValue(text) is { } t)
                    query.Add($"text={Uri.EscapeDataString(t)}");
                response = await http.PostAsync($"mode/{Uri.EscapeDataString(name)}?{string.Join('&', query)}", null, cancellationToken);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(body.Trim('"'));
                return 1;
            }
            using var json = JsonDocument.Parse(body);
            Console.WriteLine(JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Cannot reach the receiver at {target}: {ex.Message}");
            return 1;
        }
    });
    return command;
}

static string DescribeTemporary(ReceiverStatus s) =>
    (s.RemainingMessages, s.Until) switch
    {
        (int n, DateTimeOffset until) => $" for {n} more message(s) or until {until.ToLocalTime():HH:mm:ss}",
        (int n, null) => $" for {n} more message(s)",
        (null, DateTimeOffset until) => $" until {until.ToLocalTime():HH:mm:ss}",
        _ => "",
    };

Command SendCommand()
{
    var file = new Argument<FileInfo>("file") { Description = "Text file with one HL7 message, one segment per line." };
    var host = new Option<string>("--host") { Description = "Host to send to.", DefaultValueFactory = _ => "127.0.0.1" };
    var port = new Option<int>("--port") { Description = "MLLP port to send to.", DefaultValueFactory = _ => 6661 };
    var timeout = new Option<double>("--timeout") { Description = "Seconds to wait for the ACK.", DefaultValueFactory = _ => 30 };

    var command = new Command("send", "Send one HL7 message from a file and print the reply.") { file, host, port, timeout };
    command.SetAction(async (parse, cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("send");
        var path = parse.GetValue(file)!;
        var message = Hl7Text.ToWire(await File.ReadAllTextAsync(path.FullName, cancellationToken));
        var target = $"{parse.GetValue(host)}:{parse.GetValue(port)}";

        try
        {
            await using var client = await MllpClient.ConnectAsync(parse.GetValue(host)!, parse.GetValue(port), cancellationToken);
            logger.LogInformation("Connected to {Target}, sending {Bytes} bytes", target, message.Length);
            var reply = await client.SendAndReceiveAsync(
                Hl7Text.Encoding.GetBytes(message), TimeSpan.FromSeconds(parse.GetValue(timeout)), cancellationToken);

            if (reply is null)
            {
                logger.LogError("{Target} closed the connection without replying", target);
                return 1;
            }
            var replyText = Hl7Text.Encoding.GetString(reply.Payload);
            var sentId = Hl7Parsed.TryParse(message)?.ControlId ?? "";
            var ack = AckResult.Parse(replyText);
            logger.Log(ack.Status == AckStatus.Accepted ? LogLevel.Information : LogLevel.Warning,
                "{Status} ({Code}) from {Target} for {ControlId}{Text}",
                ack.Status, ack.Code, target, ack.AckedControlId, ack.Text == "" ? "" : ": " + ack.Text);
            if (ack.Status != AckStatus.Invalid && ack.AckedControlId != sentId)
                logger.LogWarning("ACK is for {Acked}, but the message sent was {Sent}", ack.AckedControlId, sentId);
            PrintSegments(replyText);
            return ack.Status == AckStatus.Accepted ? 0 : 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or System.Net.Sockets.SocketException or MllpProtocolException)
        {
            logger.LogError("Send to {Target} failed: {Reason}", target, ex.Message);
            return 1;
        }
    });
    return command;
}

Command ListenCommand()
{
    var port = new Option<int>("--port") { Description = "MLLP port to listen on.", DefaultValueFactory = _ => 6662 };
    var bind = new Option<string>("--bind") { Description = "Address to listen on (0.0.0.0 = all).", DefaultValueFactory = _ => "0.0.0.0" };

    var command = new Command("listen", "Listen for HL7 messages, print each one and reply AA.") { port, bind };
    command.SetAction(async (parse, cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("listen");
        var listener = new MllpListener(IPAddress.Parse(parse.GetValue(bind)!), parse.GetValue(port), (message, remote, _) =>
        {
            var text = Hl7Text.Encoding.GetString(message.Payload);
            logger.LogInformation("Received {Bytes} bytes from {Remote}:", message.Payload.Length, remote);
            PrintSegments(text);
            return Task.FromResult(MllpReply.Send(Hl7Text.Encoding.GetBytes(AckBuilder.For(text))));
        }, logger);

        listener.Start();
        logger.LogInformation("Listening on {Bind}:{Port}. Press Ctrl+C to stop.", parse.GetValue(bind), listener.Port);
        await listener.RunAsync(cancellationToken);
        return 0;
    });
    return command;
}

Command GenerateCommand()
{
    var count = new Option<int>("--count") { Description = "How many patients to make up.", DefaultValueFactory = _ => 10 };
    var seed = new Option<int>("--seed") { Description = "Same seed, same data.", DefaultValueFactory = _ => 1 };

    var command = new Command("generate", "Print made-up patients with one order each, to preview the fake data.") { count, seed };
    command.SetAction(parse =>
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var generator = new FakeDataGenerator(parse.GetValue(seed), today);
        var firstSlot = today.ToDateTime(new TimeOnly(8, 0));

        Console.WriteLine($"{"MRN",-9} {"Account",-11} {"Name",-26} {"DOB",-10} Sex {"Order",-12} {"Accession",-12} {"Room",-5} {"Time",-5} Procedure");
        for (var i = 0; i < parse.GetValue(count); i++)
        {
            var p = generator.NextPatient();
            var o = generator.NextOrder(firstSlot.AddMinutes(30 * i));
            var name = $"{p.LastName}, {p.FirstName} {p.MiddleInitial}";
            Console.WriteLine($"{p.Mrn,-9} {p.AccountNumber,-11} {name,-26} {p.DateOfBirth:yyyy-MM-dd} {p.Sex,-3} {o.PlacerOrderNumber,-12} {o.AccessionNumber,-12} {o.Room,-5} {o.ScheduledAt:HH:mm} {o.Procedure.Description}");
        }
        Console.WriteLine();
        Console.WriteLine("Ordering providers: " + string.Join("; ", generator.Providers.Select(d => $"{d.Id} Dr. {d.FirstName} {d.LastName}")));
        return 0;
    });
    return command;
}

Command SamplesCommand()
{
    var output = new Option<DirectoryInfo>("--out") { Description = "Folder to write the .hl7 files to.", DefaultValueFactory = _ => new DirectoryInfo("samples") };
    var seed = new Option<int>("--seed") { Description = "Same seed, same patients and orders.", DefaultValueFactory = _ => 1 };
    var version = new Option<string>("--version") { Description = "HL7 version for MSH-12, e.g. 2.3 or 2.5.1.", DefaultValueFactory = _ => "2.5.1" };

    var command = new Command("samples", "Write one example file per message type, ready for the send command.") { output, seed, version };
    command.SetAction(parse =>
    {
        var clock = TimeProvider.System;
        var generator = new FakeDataGenerator(parse.GetValue(seed), DateOnly.FromDateTime(clock.GetLocalNow().Date));
        var factory = new Hl7MessageFactory(new Hl7Settings { Version = parse.GetValue(version)! }, new ControlIdSource(clock), clock);

        var patient = generator.NextPatient();
        // A merge is the same person registered twice: same demographics, second MRN and account.
        var other = generator.NextPatient();
        var duplicate = patient with { Mrn = other.Mrn, AccountNumber = other.AccountNumber };
        var order = generator.NextOrder(clock.GetLocalNow().Date.AddHours(9));
        var updated = patient with { Phone = "555-0199", Address = patient.Address with { Street = "1 Updated Rd" } };

        var samples = new (string Name, BuiltMessage Message)[]
        {
            ("01-adt-a04-register", factory.AdtA04(patient)),
            ("02-adt-a08-update", factory.AdtA08(updated)),
            ("03-orm-o01-new-order", factory.OrmO01(patient, order, OrderControl.New)),
            ("04-siu-s12-booked", factory.SiuS12(patient, order)),
            ("05-orm-o01-in-progress", factory.OrmO01(patient, order, OrderControl.StatusChange, "IP")),
            ("06-orm-o01-cancel", factory.OrmO01(patient, order, OrderControl.Cancel)),
            ("07-adt-a40-merge", factory.AdtA40(patient, duplicate)),
        };

        var folder = parse.GetValue(output)!;
        folder.Create();
        foreach (var (name, message) in samples)
        {
            var path = Path.Combine(folder.FullName, name + ".hl7");
            File.WriteAllText(path, Hl7Text.ToDisplay(message.Text) + Environment.NewLine);
            Console.WriteLine($"{message.Type,-18} {message.ControlId}  {path}");
        }
        return 0;
    });
    return command;
}

// The single-line console logger flattens newlines, so message bodies go straight to the console.
static void PrintSegments(string wire)
{
    foreach (var line in Hl7Text.ToDisplay(wire).Split(Environment.NewLine))
        Console.WriteLine("    " + line);
}
