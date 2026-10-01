using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var dll = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "src/EsoMcp.Server/bin/Release/net10.0/eso-mcp.dll");
var live = args.Contains("--live");
var root = Path.Combine(Path.GetTempPath(), "EsoMcp.Smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var arguments = new List<string> { dll };
var configIndex = Array.IndexOf(args, "--config");
if (configIndex >= 0) arguments.AddRange(["--config", args[configIndex + 1]]);
if (!live)
{
    File.WriteAllText(Path.Combine(root, "uespLog.lua"), """
        uespLogSavedVars={data={CharName="Example",CharId="1",AccountName="@Example",Server="EU",
        SkillPointsTotal=100,SkillPointsUnused=100,AttributesHealth=64,AttributesMagicka=0,AttributesStamina=0,
        Skills={},ActionBar={}}}
        """);
    arguments.AddRange(["--database", Path.Combine(root, "test.db"), "--saved-variables", root]);
}
async Task<McpClient> Connect() => await McpClient.CreateAsync(new StdioClientTransport(new()
    { Name = "ESO protocol verification", Command = "dotnet", Arguments = arguments }));
async Task<JsonElement> Call(McpClient client, string tool, Dictionary<string, object?>? parameters = null, bool error = false)
{
    var timer = Stopwatch.StartNew();
    var result = await client.CallToolAsync(tool, parameters);
    var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    Console.WriteLine(JsonSerializer.Serialize(new { tool, ms = Math.Round(timer.Elapsed.TotalMilliseconds), chars = text.Length, error = result.IsError }));
    if (result.IsError == true != error) throw new InvalidOperationException(text);
    return error ? default : JsonDocument.Parse(text).RootElement.Clone();
}
string? planId = null;
try
{
    await using (var client = await Connect())
    {
        var tools = await client.ListToolsAsync();
        foreach (var name in new[] { "inspect_account", "edit_build", "analyze_build", "export_build", "verify_build", "resolve_definitions", "refresh_catalog", "query_prices" })
            if (!tools.Any(t => t.Name == name)) throw new InvalidOperationException($"Missing tool {name}.");
        var requestIndex = Array.IndexOf(args, "--request");
        if (requestIndex >= 0)
        {
            var request = JsonDocument.Parse(File.ReadAllText(args[requestIndex + 1])).RootElement;
            var parameters = request.GetProperty("arguments").EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            var response = await Call(client, request.GetProperty("tool").GetString()!, parameters);
            Console.WriteLine(response.GetRawText()); return 0;
        }
        var discovery = await Call(client, "inspect_account");
        var account = discovery.GetProperty("accounts")[0].GetProperty("key").GetString()!;
        var listed = await Call(client, "inspect_account", new() { ["account"] = account, ["queries"] = new[] { new { section = "characters" } } });
        var characters = listed.GetProperty("results")[0].GetProperty("rows").EnumerateArray().ToArray();
        var character = (live ? characters.FirstOrDefault(c => c.GetProperty("name").GetString() == "Thicker-Than-Urmom") : characters[0]);
        if (character.ValueKind == JsonValueKind.Undefined) character = characters[0];
        var charId = character.GetProperty("id").GetString()!;
        var created = await Call(client, "edit_build", new() { ["action"] = "create", ["account"] = account, ["character"] = charId, ["name"] = "Temporary protocol verification" });
        planId = created.GetProperty("plan").GetProperty("id").GetString()!;
        var verified = await Call(client, "verify_build", new() { ["id"] = planId, ["expectedRevision"] = 1 });
        if (!verified.GetProperty("matchesObserved").GetBoolean()) throw new InvalidOperationException("Current-state copy does not match observed character.");
        await Call(client, "edit_build", new() { ["action"] = "update", ["id"] = planId, ["expectedRevision"] = 1,
            ["patch"] = new { attributes = new { health = 0, magicka = 64, stamina = 0 } } });
        await Call(client, "edit_build", new() { ["action"] = "update", ["id"] = planId, ["expectedRevision"] = 1, ["name"] = "Stale edit" }, error: true);
        var exported = await Call(client, "export_build", new() { ["id"] = planId, ["sections"] = "Attributes" });
        if (!exported.GetProperty("text").GetString()!.Contains("0;64;0")) throw new InvalidOperationException("Attribute export mismatch.");
        await Call(client, "analyze_build", new() { ["id"] = planId, ["section"] = "differences" });
    }
    await using (var restarted = await Connect())
    {
        var restored = await Call(restarted, "edit_build", new() { ["action"] = "read", ["id"] = planId });
        if (restored.GetProperty("revision").GetInt32() != 2) throw new InvalidOperationException("Draft did not survive restart.");
        await Call(restarted, "edit_build", new() { ["action"] = "delete", ["id"] = planId, ["expectedRevision"] = 2 });
        planId = null;
        var discovery = await Call(restarted, "inspect_account");
        var owner = discovery.GetProperty("accounts")[0].GetProperty("key").GetString()!;
        var people = await Call(restarted, "inspect_account", new() { ["account"] = owner, ["queries"] = new[] { new { section = "characters" } } });
        var characterId = people.GetProperty("results")[0].GetProperty("rows")[0].GetProperty("id").GetString()!;
        var target = await Call(restarted, "edit_build", new() { ["action"] = "create", ["account"] = owner,
            ["character"] = characterId, ["name"] = "Temporary guide verification", ["patch"] = new { target = new
            { variant = "Example guide", abilities = new[] { new { slot = "front.1", name = "Example scribed ability", scripts = new { focus = "Taunt", signature = "Heal", affix = "Maim" } } } } } });
        planId = target.GetProperty("plan").GetProperty("id").GetString()!;
        var guide = await Call(restarted, "edit_build", new() { ["action"] = "read", ["id"] = planId, ["section"] = "target" });
        if (guide.GetProperty("abilities")[0].GetProperty("scripts").GetProperty("focus").GetString() != "Taunt") throw new InvalidOperationException("Guide recipe did not survive the protocol.");
        await Call(restarted, "analyze_build", new() { ["id"] = planId, ["targetSection"] = "abilities" });
        await Call(restarted, "export_build", new() { ["id"] = planId }, error: true);
        await Call(restarted, "edit_build", new() { ["action"] = "delete", ["id"] = planId, ["expectedRevision"] = 1 });
        planId = null;
    }
    Console.WriteLine("PASS: discovery, fresh reads, atomic editing, conflict protection, export, comparison, restart persistence and complete guide-target serialization.");
    return 0;
}
finally
{
    if (planId is not null) Console.Error.WriteLine($"Temporary verification plan retained after failure: {planId}");
    // root is an exclusively created temporary fixture directory, never an ESO source directory.
    Directory.Delete(root, true);
}
