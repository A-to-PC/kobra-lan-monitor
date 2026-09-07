using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KobraLanMonitor;

public record LanCredentials(
    string Broker,
    string DeviceId,
    string Username,
    string Password,
    string DeviceCrt,
    string DevicePk,
    string ModelId,
    string ModelName);

public static class LanCredentialDiscovery
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<LanCredentials> DiscoverAsync(string printerHost, CancellationToken ct)
    {
        var infoJson = await Http.GetStringAsync($"http://{printerHost}:18910/info", ct);
        using var info = JsonDocument.Parse(infoJson);
        var token = info.RootElement.GetProperty("token").GetString()!;
        var ctrlUrl = info.RootElement.GetProperty("ctrlInfoUrl").GetString()!;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var nonce = RandomAlphaNumeric(6);
        var deviceId = "KobraLanMonitor-" + RandomAlphaNumeric(8);
        var sign = Md5Hex(Md5Hex(token[..16]) + timestamp + nonce);

        var query = $"ts={timestamp}&nonce={nonce}&sign={sign}&did={deviceId}";
        using var ctrlResponse = await Http.PostAsync($"{ctrlUrl}?{query}", content: null, ct);
        ctrlResponse.EnsureSuccessStatusCode();
        var ctrlJson = await ctrlResponse.Content.ReadAsStringAsync(ct);
        using var ctrl = JsonDocument.Parse(ctrlJson);

        var code = ctrl.RootElement.GetProperty("code").GetInt32();
        if (code != 200)
        {
            throw new InvalidOperationException($"/ctrl returned code {code}: {ctrlJson}");
        }

        var data = ctrl.RootElement.GetProperty("data");
        var cipherB64 = data.GetProperty("info").GetString()!;
        var iv = data.GetProperty("token").GetString()!;
        var key = token[16..32];

        var plainJson = DecryptAesCbc(cipherB64, key, iv);
        using var creds = JsonDocument.Parse(plainJson);
        var root = creds.RootElement;

        return new LanCredentials(
            Broker: root.GetProperty("broker").GetString()!,
            DeviceId: root.GetProperty("deviceId").GetString()!,
            Username: root.GetProperty("username").GetString()!,
            Password: root.GetProperty("password").GetString()!,
            DeviceCrt: root.GetProperty("devicecrt").GetString()!,
            DevicePk: root.GetProperty("devicepk").GetString()!,
            ModelId: root.GetProperty("modeId").GetString()!,
            ModelName: root.GetProperty("modelName").GetString()!);
    }

    private static string DecryptAesCbc(string cipherB64, string key, string iv)
    {
        var cipherBytes = Convert.FromBase64String(cipherB64);

        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = Encoding.UTF8.GetBytes(iv);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static string Md5Hex(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }

    private static string RandomAlphaNumeric(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(length, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }
}
