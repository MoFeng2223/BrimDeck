using BrimDeck;
using BrimDeck.Modules;

internal static partial class Program
{
    private sealed class SchemaSettings(IReadOnlyList<ModuleSetting> fields, IReadOnlyDictionary<string, object> values) : IModuleSettings
    {
        public string Name => "Schema";
        public string NameEn => "Schema";
        public bool Visible => true;
        public IReadOnlyList<ModuleSetting> Fields => fields;
        public object Read(string key) => values[key];
        public void Write(string key, object value) { }
        public void Reset() { }
    }
    private sealed class SlowSettings(IReadOnlyList<ModuleSetting> fields, IReadOnlyDictionary<string, object> values) : IModuleSettings
    {
        public string Name => "Slow";
        public string NameEn => "Slow";
        public bool Visible => true;
        public IReadOnlyList<ModuleSetting> Fields => fields;
        public object Read(string key) { Thread.Sleep(12); return values[key]; }
        public void Write(string key, object value) { }
        public void Reset() { }
    }
    private static void SettingsSchemaChecks()
    {
        var fields = new ModuleSetting[]
        {
            new("enabled", "开启", "Enabled", ModuleSettingKind.Toggle),
            new("number", "数字", "Number", ModuleSettingKind.Number, 0, 10) { Step = .25, Group = "基础", Description = "数值说明" },
            new("choice", "选项", "Choice", ModuleSettingKind.Choice) { Options = [new(1, "一", ""), new(2, "二", "Two")] },
            new("text", "文字", "Text", ModuleSettingKind.Text) { MaximumLength = 32, Placeholder = "请输入" },
            new("color", "颜色", "Color", ModuleSettingKind.Color),
            new("enabled", "重复", "Duplicate", ModuleSettingKind.Toggle),
            new("unknown", "未知", "Unknown", (ModuleSettingKind)999),
            new("range", "范围", "Range", ModuleSettingKind.Number, 10, 1),
            new("step", "步长", "Step", ModuleSettingKind.Number) { Step = double.NaN },
            new("options", "选项错误", "Bad options", ModuleSettingKind.Choice) { Options = [new(1, "一", "One"), new(1, "二", "Two")] },
            new("bad-color", "颜色错误", "Bad color", ModuleSettingKind.Color),
            new("null-options", "缺少选项", "Missing options", ModuleSettingKind.Choice) { Options = null! },
            new("date", "错误数值", "Invalid numeric value", ModuleSettingKind.Number),
            new("character", "字符数值", "Character value", ModuleSettingKind.Number, 0, 1000),
            null!,
        };
        var values = new Dictionary<string, object> { ["enabled"] = true, ["number"] = 1.5, ["choice"] = 2, ["text"] = "kept", ["color"] = "#aBcDeF", ["bad-color"] = "not color", ["date"] = DateTime.UtcNow, ["character"] = 'A' };
        using var host = Host();
        host.ReconcileAsync([Def("schema", c => { c.Register<IModuleSettings>("main", new SchemaSettings(fields, values)); return ValueTask.CompletedTask; })]).GetAwaiter().GetResult();
        var entry = host.Registry.List<IModuleSettings>().Single(); var projection = ModuleSettingsSchema.Read(host, entry);
        Check("validated settings retain all five supported field kinds", projection.Take(5).Select(f => f.Definition.Kind).SequenceEqual(new[] { ModuleSettingKind.Toggle, ModuleSettingKind.Number, ModuleSettingKind.Choice, ModuleSettingKind.Text, ModuleSettingKind.Color }));
        Check("a stored value outside its declaration is coerced and stays editable",
            projection.Count == 8 && (string)projection[5].Value == "#FFFFFF" && (double)projection[6].Value == 0 && (double)projection[7].Value == 0);
        Check("text and numeric metadata survive the shared projection", projection[1].Definition.Step == .25 && projection[3].Definition.MaximumLength == 32);
        Check("optional translated metadata has a documented fallback", projection[1].Definition.GroupEn == "基础" && projection[3].Definition.PlaceholderEn == "请输入");
        Check("choice option label falls back without changing its source definition", projection[2].Definition.Options[0].LabelEn == "一" && fields[2].Options[0].LabelEn == "");
        Check("color values are normalized without writing module data", (string)projection[4].Value == "#ABCDEF" && (string)values["color"] == "#aBcDeF");
        Throws("choice options projection is immutable", () => ((IList<ModuleSettingOption>)projection[2].Definition.Options).Clear());
        Check("bad fields degrade independently while the module stays ready", host.Modules.Single().Status == ModuleStatus.Ready && host.Faults.Count == 0 && host.Modules.Single().Diagnostics.Count == 10);
        for (int i = 0; i < 4; i++) _ = ModuleSettingsSchema.Read(host, entry);
        Check("reopening malformed settings does not duplicate diagnostics", host.Modules.Single().Diagnostics.Count == 10);
        Check("color field rejects alpha, named and malformed color strings", !ModuleSettingsSchema.Color("#80FFFFFF") && !ModuleSettingsSchema.Color("red") && !ModuleSettingsSchema.Color("#GGGGGG"));
        var many = Enumerable.Range(0, 140).Select(i => new ModuleSetting("key" + i, "Flag", "Flag", ModuleSettingKind.Toggle)).ToArray();
        var manyValues = Enumerable.Range(0, 140).ToDictionary(i => "key" + i, _ => (object)true);
        host.ReconcileAsync([Def("schema", c => { c.Register<IModuleSettings>("main", new SchemaSettings(many, manyValues)); return ValueTask.CompletedTask; }, "next")]).GetAwaiter().GetResult();
        Check("settings presentation has a bounded field count", ModuleSettingsSchema.Read(host, host.Registry.List<IModuleSettings>().Single()).Count == 128 && host.Modules.Single().Diagnostics.Single().Code == "settings-field-limit");
        {
            using var slow = Host();
            var slowFields = Enumerable.Range(0, 60).Select(i => new ModuleSetting("k" + i, "Flag", "Flag", ModuleSettingKind.Toggle)).ToArray();
            var slowValues = Enumerable.Range(0, 60).ToDictionary(i => "k" + i, _ => (object)true);
            slow.ReconcileAsync([Def("slow", c => { c.Register<IModuleSettings>("main", new SlowSettings(slowFields, slowValues)); return ValueTask.CompletedTask; })]).GetAwaiter().GetResult();
            var slowProjection = ModuleSettingsSchema.Read(slow, slow.Registry.List<IModuleSettings>().Single());
            Check("a slow settings getter is cut off by the read budget",
                slowProjection.Count is > 0 and < 60 && slow.Modules.Single().Diagnostics.Any(d => d.Code == "settings-read-slow"));
        }
    }
}
