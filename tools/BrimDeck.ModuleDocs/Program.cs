using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BrimDeck;
using BrimDeck.Modules;

const string begin = "<!-- BEGIN GENERATED MODULE API -->";
const string end = "<!-- END GENERATED MODULE API -->";
bool check = args.Contains("--check");
string root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ".");
string file = Path.Combine(root, "docs", "zh-CN", "modules", "api.md");
string current = File.ReadAllText(file).Replace("\r\n", "\n");
int first = current.IndexOf(begin, StringComparison.Ordinal), last = current.IndexOf(end, StringComparison.Ordinal);
if (first < 0 || last < first || current.IndexOf(begin, first + begin.Length, StringComparison.Ordinal) >= 0 || current.IndexOf(end, last + end.Length, StringComparison.Ordinal) >= 0)
    throw new InvalidDataException("API document needs exactly one generated section.");

Type[] types = [typeof(ModuleSettingsStore), typeof(IModuleVersionedSettings), typeof(IModuleCustomSettings), typeof(ModuleSettingsSnapshot), typeof(ModuleSettingsPatch), typeof(ModuleSettingsWriteStatus), typeof(ModuleSettingsWriteResult), typeof(IModuleSettingsNavigation), typeof(ModuleSettingsPlacement), typeof(ModuleDiagnostic), typeof(ModuleDiagnosticSeverity),
    typeof(IModulePlatform), typeof(IModuleRegistries), typeof(IModuleRegistration), typeof(IModuleEvents), typeof(IModuleServices),
    typeof(ModuleContextExtensions), typeof(ModuleEventPriority), typeof(ModuleOpeningEvent), typeof(ModulePageOpening), typeof(ModuleActivityOpening), typeof(IModulePresentation),
    typeof(ModuleSettingOption), typeof(ModuleTheme), typeof(ModuleLocalization), typeof(ModuleMotion), typeof(Handover), typeof(PageHandover), typeof(RollingDigitsView), typeof(RollingStepper), typeof(RollingStepper.EditDraft),
    typeof(IModule), typeof(IModuleContext), typeof(IModuleStorage), typeof(IModuleScheduler),
    typeof(ModuleManifest), typeof(ModuleDependency), typeof(ModuleHostRequirement), typeof(ModuleAction), typeof(ModuleSuspending), typeof(ModulePreferencesChanged), typeof(ModuleUiChanged),
    typeof(ModulePage), typeof(IModuleView), typeof(ModuleViewContext), typeof(IModuleActivity), typeof(IModuleAsyncActivity), typeof(ActivityState), typeof(ActivityViewKind),
    typeof(ModuleWidget), typeof(IModuleSettings), typeof(ModuleSetting), typeof(ModuleSettingKind), typeof(ModuleUi), typeof(ModuleControlButton),
    typeof(ModuleInfo), typeof(ModuleStatus), typeof(ModuleFault), typeof(Contribution<>), typeof(ModulePerformance),
    typeof(ModuleApi), typeof(ModuleApiException), typeof(ModuleRequirements), typeof(IModuleHostEvent),
    typeof(ModuleDeprecations), typeof(ModuleDeprecation), typeof(ModuleDeprecationKind),
    typeof(ModuleSignature), typeof(ModuleSignatureStatus), typeof(ModuleSignatureChannel), typeof(ModuleSigningKey), typeof(ModuleRevocation),
    typeof(ModuleViewport), typeof(IModuleLayoutView), typeof(IModuleStructuredStorage), typeof(ModuleReadResult<>), typeof(ModuleReadStatus), typeof(ModuleStorageExtensions),
    typeof(IModuleHostInfo), typeof(ModuleHostInfo), typeof(IModuleSnapshotReader<>), typeof(ModuleDataStatus),
    typeof(IModuleUsage), typeof(ModuleUsageSnapshot), typeof(ModuleUsageEntry), typeof(ModuleUsageMetric),
    typeof(IModuleMedia), typeof(ModuleMediaSnapshot), typeof(IModuleSystem), typeof(ModuleSystemSnapshot),
    typeof(IModuleNotifications), typeof(ModuleNotice), typeof(ModuleNoticeResult), typeof(ModuleNoticeTone), typeof(ModuleCapability), typeof(ModuleCapabilityKind)];
var nullable = new NullabilityInfoContext();
var text = new StringBuilder(begin + "\n\n");
text.AppendLine("此区块由 `tools/BrimDeck.ModuleDocs` 从指定契约类型生成；不要手工编辑。基础类型为模组作者使用，末尾的状态与统计 DTO 为宿主诊断使用。\n");
foreach (Type type in types)
{
    text.AppendLine("### `" + Name(type) + "`\n");
    text.AppendLine("程序集：`" + type.Assembly.GetName().Name + "`；命名空间：`" + type.Namespace + "`。\n");
    if (type.IsInterface && type.GetInterfaces() is { Length: > 0 } parents) text.AppendLine("继承：" + string.Join("、", parents.Select(parent => "`" + Name(parent) + "`")) + "。\n");
    text.AppendLine("| 成员签名 |\n| --- |");
    const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    if (type.IsEnum)
    {
        foreach (FieldInfo field in type.GetFields(flags).Where(f => f.IsLiteral)) Row(field.Name + " = " + Convert.ToInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture));
    }
    else
    {
        foreach (FieldInfo field in type.GetFields(flags).Where(f => f.IsLiteral)) Row("const " + Name(field.FieldType) + " " + field.Name + " = " + Literal(field.GetRawConstantValue()));
        foreach (ConstructorInfo ctor in type.GetConstructors(flags).OrderBy(c => c.GetParameters().Length)) Row(Name(type) + "(" + Parameters(ctor.GetParameters()) + ")");
        foreach (PropertyInfo property in type.GetProperties(flags).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var modifier = property.GetMethod?.IsStatic == true ? "static " : "";
            bool init = property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit") == true;
            Row(modifier + Name(property.PropertyType, nullable.Create(property)) + " " + property.Name + " { "
                + (property.GetMethod?.IsPublic == true ? "get; " : "") + (property.SetMethod?.IsPublic == true ? init ? "init; " : "set; " : "") + "}");
        }
        foreach (EventInfo item in type.GetEvents(flags).OrderBy(e => e.Name, StringComparer.Ordinal)) Row("event " + Name(item.EventHandlerType!, nullable.Create(item)) + " " + item.Name);
        foreach (MethodInfo method in type.GetMethods(flags).Where(m => !m.IsSpecialName && m.GetBaseDefinition().DeclaringType == type
            && m.Name is not ("ToString" or "GetHashCode" or "Equals" or "Deconstruct" or "<Clone>$")).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            string generics = method.IsGenericMethod ? "<" + string.Join(", ", method.GetGenericArguments().Select(t => t.Name)) + ">" : "";
            string constraints = string.Concat(method.GetGenericArguments().Where(t => (t.GenericParameterAttributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0).Select(t => " where " + t.Name + " : class"));
            string result = Name(method.ReturnType, nullable.Create(method.ReturnParameter));
            if (method.ReturnType.IsGenericParameter && nullable.Create(method.ReturnParameter).ReadState == NullabilityState.Nullable) result += "?";
            Row((method.IsStatic ? "static " : "") + result + " " + method.Name + generics + "(" + Parameters(method.GetParameters()) + ")" + constraints);
        }
    }
    text.AppendLine();
}
text.Append(end);
string updated = current[..first] + text.ToString().Replace("\r\n", "\n") + current[(last + end.Length)..];
const string catalogBegin = "<!-- BEGIN GENERATED MODULE CAPABILITIES -->", catalogEnd = "<!-- END GENERATED MODULE CAPABILITIES -->";
int catalogFirst = updated.IndexOf(catalogBegin, StringComparison.Ordinal), catalogLast = updated.IndexOf(catalogEnd, StringComparison.Ordinal);
if (catalogFirst < 0 || catalogLast < catalogFirst || updated.IndexOf(catalogBegin, catalogFirst + catalogBegin.Length, StringComparison.Ordinal) >= 0
    || updated.IndexOf(catalogEnd, catalogLast + catalogEnd.Length, StringComparison.Ordinal) >= 0) throw new InvalidDataException("API document needs exactly one capability catalog section.");
var catalog = new StringBuilder(catalogBegin + "\n\n");
catalog.AppendLine("此表由 `ModuleApi.Catalog` 生成，与清单校验、宿主绑定校验及权限名称共用同一来源。列出 SDK 已知能力；本机实际可用项以 `IModuleHostInfo.GetInfo()` 为准。\n");
catalog.AppendLine("| ID | 类型 | 名称 / English | 引入 minor | 必要权限 | 环境 | 资源归属 | 公开契约 |\n| --- | --- | --- | --- | --- | --- | --- | --- |");
foreach (var entry in ModuleApi.Catalog)
    catalog.AppendLine($"| `{entry.Id}` | {entry.Kind} | {entry.Name} / {entry.NameEn} | {entry.IntroducedMinor} | {(entry.Permission is null ? "无" : "`" + entry.Permission + "`")} | `{entry.Environment}` | `{entry.ResourceLifetime}` | `{entry.ContractAssembly}`: `{entry.ContractType}` |");
catalog.AppendLine("\n当前目录中的能力均为实验接口。`activation` 表示贡献或服务归属本次模组激活，具体视图另由视图上下文释放。");
catalog.Append(catalogEnd);
updated = updated[..catalogFirst] + catalog.ToString().Replace("\r\n", "\n") + updated[(catalogLast + catalogEnd.Length)..];
if (check)
{
    if (updated != current) { Console.Error.WriteLine("Module API signatures or capability catalog are stale. Run the documentation tool without --check."); return 1; }
    Console.WriteLine($"Module API signatures match {types.Length} contract types and {ModuleApi.Catalog.Count} capability descriptors.");
}
else { File.WriteAllText(file, updated, new UTF8Encoding(false)); Console.WriteLine($"Updated module API signatures from {types.Length} contract types."); }
return 0;

void Row(string signature) => text.AppendLine("| `" + signature.Replace("|", "\\|") + "` |");
string Parameters(ParameterInfo[] values) => string.Join(", ", values.Select(p => Name(p.ParameterType, nullable.Create(p)) + " " + p.Name + (p.HasDefaultValue ? " = " + Literal(p.DefaultValue) : "")));
string Literal(object? value) => value switch { null => "null", string s => JsonSerializer.Serialize(s), bool b => b ? "true" : "false", _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null" };
string Name(Type type, NullabilityInfo? info = null)
{
    if (type.IsByRef) return Name(type.GetElementType()!, info?.ElementType);
    if (type.IsArray) return Name(type.GetElementType()!, info?.ElementType) + "[]" + Suffix();
    if (Nullable.GetUnderlyingType(type) is { } value) return Name(value) + "?";
    string name = type == typeof(void) ? "void" : type == typeof(string) ? "string" : type == typeof(bool) ? "bool" : type == typeof(int) ? "int"
        : type == typeof(long) ? "long" : type == typeof(double) ? "double" : type == typeof(object) ? "object" : type.Name.Split('`')[0];
    if (type.IsGenericType) name += "<" + string.Join(", ", type.GetGenericArguments().Select((argument, index) => Name(argument, info?.GenericTypeArguments.ElementAtOrDefault(index)))) + ">";
    return name + Suffix();
    string Suffix() => info?.ReadState == NullabilityState.Nullable && !type.IsValueType && !type.IsGenericParameter ? "?" : "";
}
