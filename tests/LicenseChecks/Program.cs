using SocMacro.License;

int passed = 0;
void Check(string name, Action test)
{
    test();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Assert(bool condition)
{
    if (!condition) throw new Exception("Assertion failed");
}
void Reject(string input)
{
    try { LicenseAdmin.PrepareNewKey(input); }
    catch (LicenseException) { return; }
    throw new Exception("Invalid key was accepted");
}

Check("4-character and 16-character boundaries accepted", () =>
{
    Assert(LicenseAdmin.PrepareNewKey("abcd") == "ABCD");
    Assert(LicenseAdmin.PrepareNewKey("abcdefghijklmnop") == "ABCDEFGHIJKLMNOP");
});
Check("17-character and much longer custom keys rejected", () =>
{
    Reject("ABCDEFGHIJKLMNOPQ");
    Reject(new string('A', 1000));
});
Check("spaces, tabs and hyphens do not count toward the limit", () =>
{
    Assert(LicenseAdmin.PrepareNewKey("abcd efgh-ijkl\tmnop") == "ABCDEFGHIJKLMNOP");
    Reject("abcd efgh-ijkl mnop-q");
    Reject("a - b\tc");
});
Check("existing 12-character format preserved", () =>
{
    foreach (var input in new[] { "777177717771", "7771 7771 7771", "7771-7771-7771" })
        Assert(LicenseAdmin.PrepareNewKey(input) == "7771-7771-7771");
});
Check("empty custom key generates a valid random key", () =>
{
    foreach (var input in new string?[] { null, "", " \t" })
    {
        var key = LicenseAdmin.PrepareNewKey(input);
        Assert(key.Length == 14 && key[4] == '-' && key[9] == '-');
        Assert(key.Count(char.IsAsciiLetterOrDigit) == 12);
    }
});
Check("legacy long keys remain unchanged for login", () =>
{
    Assert(LicenseService.NormalizeKey("abcdefghijklmnopq") == "ABCDEFGHIJKLMNOPQ");
});

// Invalid issuance must fail before any server request or Firebase CLI credential access.
try { await new LicenseAdmin().CreateKey(new string('A', 17), LicenseTier.Basic, "test"); }
catch (LicenseException ex) when (ex.Message.Contains("16"))
{
    Console.WriteLine("PASS invalid issuance rejected before server access");
    passed++;
    Console.WriteLine($"{passed} checks passed.");
    return;
}
throw new Exception("Invalid issuance did not fail validation");
