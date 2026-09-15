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
        }
        if (mode == "effect") File.WriteAllText(args[1], parameters.GetRawText());
        if (mode is "cl-update" or "cl-update-fail" or "cl-update-bad")
        {
            var updated = parameters.EnumerateArray().Select(p => (object)p.Clone()).ToArray();
            var first = parameters[0]; var bytes = first.GetProperty("data").GetBytesFromBase64();
            if (Convert.ToHexString(bytes) != "00000003") return 13;
            updated[0] = new { type = "buffer", ccsid = first.GetProperty("ccsid").GetInt32(), data = Convert.FromHexString("0000000C") };
            if (mode == "cl-update-bad") updated[1] = new { type = "buffer", ccsid = 37, data = new byte[] { 0 } };
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
