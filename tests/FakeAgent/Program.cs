using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

// A stand-in for an agent CLI. rtfd runs it as `claude ... --scenario <file>`. The scenario
// file says what to print on each call, in order:
//
//   {
//     "record": "<path>",               // appends one JSON line per call: args, env, cwd, stdin
//     "responses": [
//       { "result": "...", "exit": 0 },  // printed in claude's JSON wrapper, as `result`
//       { "stdout": "...", "stderr": "...", "exit": 1 },  // printed as is
//       { "sleep_ms": 60000, "spawn_pidfile": "<path>" }  // hangs, after starting a child that hangs too
//     ]
//   }
//
// The last response repeats once the list runs out.

var scenarioIndex = Array.IndexOf(args, "--scenario");
if (scenarioIndex < 0 || scenarioIndex + 1 >= args.Length)
{
    Console.Error.WriteLine("FakeAgent: --scenario <file> is required");
    return 2;
}

if (args.Contains("--child"))
{
    Thread.Sleep(TimeSpan.FromMinutes(5));
    return 0;
}

var scenarioPath = args[scenarioIndex + 1];
var scenario = JsonNode.Parse(File.ReadAllText(scenarioPath))!.AsObject();

// What `rtfd validate` asks: the version, the help, and the init event of a streaming run.
if (args.Contains("--version"))
{
    Console.Out.Write((scenario["version"]?.GetValue<string>() ?? "9.9.9 (Fake Agent)") + "\n");
    return 0;
}

if (args.Contains("--help"))
{
    Console.Out.Write(scenario["help"]?.GetValue<string>() ?? "");
    return 0;
}

var format = Array.IndexOf(args, "--output-format");
if (format >= 0 && format + 1 < args.Length && args[format + 1] == "stream-json" && scenario["init"] is JsonObject init)
{
    // Like Claude: the init event, then a model call that takes a while. rtfd stops it first.
    Console.Out.Write(init.ToJsonString() + "\n");
    Console.Out.Flush();
    Thread.Sleep(TimeSpan.FromSeconds(scenario["init_then_sleep_s"]?.GetValue<int>() ?? 60));
    return 0;
}

var stdin = Console.IsInputRedirected ? Console.In.ReadToEnd() : "";

// Which call this is, kept next to the scenario.
var counterPath = scenarioPath + ".count";
var call = File.Exists(counterPath) ? int.Parse(File.ReadAllText(counterPath)) : 0;
File.WriteAllText(counterPath, (call + 1).ToString());

if (scenario["record"]?.GetValue<string>() is { } recordPath)
{
    var environment = new JsonObject();
    foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
    {
        environment[(string)entry.Key] = (string?)entry.Value;
    }

    var record = new JsonObject
    {
        ["args"] = new JsonArray([.. args.Select(a => (JsonNode)a)]),
        ["env"] = environment,
        ["cwd"] = Environment.CurrentDirectory,
        ["stdin"] = stdin,
    };
    File.AppendAllText(recordPath, record.ToJsonString() + "\n");
}

var responses = scenario["responses"]!.AsArray();
var response = responses[Math.Min(call, responses.Count - 1)]!.AsObject();

if (response["spawn_pidfile"]?.GetValue<string>() is { } pidFile)
{
    var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["--scenario", scenarioPath, "--child"])
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;
    File.WriteAllText(pidFile, child.Id.ToString());
}

if (response["sleep_ms"]?.GetValue<int>() is { } sleep)
{
    Thread.Sleep(sleep);
}

var stdout = Console.OpenStandardOutput();
if (response["result"]?.GetValue<string>() is { } result)
{
    var wrapper = new JsonObject
    {
        ["type"] = "result",
        ["subtype"] = response["subtype"]?.GetValue<string>() ?? "success",
        ["is_error"] = response["is_error"]?.GetValue<bool>() ?? false,
        ["result"] = result,
    };
    stdout.Write(Encoding.UTF8.GetBytes(wrapper.ToJsonString()));
}
else if (response["stdout"]?.GetValue<string>() is { } text)
{
    stdout.Write(Encoding.UTF8.GetBytes(text));
}

if (response["stderr"]?.GetValue<string>() is { } error)
{
    Console.Error.Write(error);
}

return response["exit"]?.GetValue<int>() ?? 0;
