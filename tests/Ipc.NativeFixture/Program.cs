using System.Text.Json;

namespace Ipc.NativeFixture;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var request = JsonDocument.Parse(await Console.In.ReadToEndAsync());
        var version = request.RootElement.GetProperty("version").GetInt32();
        if (version is not (1 or 2)) return 12;
        var parameters = request.RootElement.GetProperty("parameters");
        var mode = args[0];
        if (mode == "fail") { Console.Error.Write("fixture failure"); return 7; }
        if (mode == "malformed") { Console.Write("invalid response"); return 0; }
        if (mode == "sleep")
        {
            File.WriteAllText(args[1], Environment.ProcessId.ToString());
            await Task.Delay(TimeSpan.FromMinutes(2));
        }
        if (mode == "tree")
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true };
            foreach (var argument in new[] { typeof(Program).Assembly.Location, "sleep", args[1] + ".child" }) start.ArgumentList.Add(argument);
            using var child = System.Diagnostics.Process.Start(start)!;
            await child.StandardInput.WriteLineAsync(request.RootElement.GetRawText()); child.StandardInput.Close();
            while (!File.Exists(args[1] + ".child")) await Task.Delay(10);
            File.WriteAllText(args[1], Environment.ProcessId.ToString());
            await child.WaitForExitAsync();
        }
        if (mode == "flood") { Console.Write(new string('X', 1100000)); return 0; }
        if (mode == "cpu")
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 500) Thread.SpinWait(1000);
            // The accounting test releases this child only after observing a live sample.
            if (args.Length > 1) while (!File.Exists(args[1])) await Task.Delay(10);
        }
        if (mode == "effect") File.WriteAllText(args[1], parameters.GetRawText());
        if (mode is "rpg-aggregate-update" or "rpg-aggregate-bad")
        {
            var first = parameters[0]; var data = first.GetProperty("data").GetBytesFromBase64();
            if (Convert.ToHexString(data) != "030303") return 17;
            data[1] = 12;
            Console.Write(JsonSerializer.Serialize(new { version, success = true, parameters = new object[] {
                new { type = "buffer", ccsid = first.GetProperty("ccsid").GetInt32(), data }, mode == "rpg-aggregate-bad" ? (object)"invalid integer" : 4 } }));
            return 0;
        }
        if (mode is "rpg-omit-echo" or "rpg-omit-change")
        {
            if (parameters.GetArrayLength() != 3 || parameters[0].GetInt32() != 7 || parameters[1].ValueKind != JsonValueKind.Null || parameters[2].ValueKind != JsonValueKind.Null) return 16;
            Console.Write(JsonSerializer.Serialize(new { version, success = true, message = "OMIT response", parameters = new object?[] { 12, null, mode == "rpg-omit-change" ? 1 : null } }));
            return 0;
        }
        if (mode is "rpg-options-echo" or "rpg-options-change")
        {
            if (parameters[0].GetInt32() != 3 || parameters[1].GetString() != "  AB") return 18;
            Console.Write(JsonSerializer.Serialize(new { version, success = true, parameters = new object[] { 12, mode == "rpg-options-change" ? "XXXX" : "  AB" } }));
            return 0;
        }
        if (mode is "rpg-const-echo" or "rpg-const-change")
        {
            if (parameters[0].GetInt32() != 3 || parameters[1].GetInt32() != 4) return 15;
            Console.Write(JsonSerializer.Serialize(new { version, success = true, message = "CONST response", parameters = new[] { 12, mode == "rpg-const-change" ? 5 : 4 } }));
            return 0;
        }
        if (mode is "rpg-update" or "rpg-update-fail" or "rpg-update-bad")
        {
            if (parameters[0].GetInt32() != 3 || parameters[1].GetInt32() != 4) return 14;
            object?[] updated = [12, mode == "rpg-update-bad" ? "invalid integer" : 4];
            Console.Write(JsonSerializer.Serialize(new { version, success = mode != "rpg-update-fail", message = "CPF9898: fixture update", parameters = updated }));
            return 0;
        }
        if (mode is "cl-update" or "cl-update-fail" or "cl-update-bad" or "cl-const-echo" or "cl-const-change")
        {
            var updated = parameters.EnumerateArray().Select(p => (object)p.Clone()).ToArray();
            var first = parameters[0]; var bytes = first.GetProperty("data").GetBytesFromBase64();
            if (Convert.ToHexString(bytes) != "00000003") return 13;
            updated[0] = new { type = "buffer", ccsid = first.GetProperty("ccsid").GetInt32(), data = Convert.FromHexString("0000000C") };
            if (mode == "cl-update-bad") updated[1] = new { type = "buffer", ccsid = 37, data = new byte[] { 0 } };
            if (mode == "cl-const-change") updated[1] = new { type = "buffer", ccsid = first.GetProperty("ccsid").GetInt32(), data = Convert.FromHexString("00000005") };
            Console.Write(JsonSerializer.Serialize(new { version, success = mode != "cl-update-fail", message = "CPF9898: fixture update", parameters = updated }));
            return 0;
        }
        Console.Write(JsonSerializer.Serialize(new
        {
            version, success = true,
            message = "native: " + parameters.GetRawText(), parameters,
        }));
        return 0;
    }
}
