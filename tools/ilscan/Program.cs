using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Program
{
    /// <summary>
    /// Game install root. Set the KOIKATSU_DIR environment variable to point at your game folder,
    /// or pass an absolute dll path instead of an alias.
    /// </summary>
    static string GameRoot =>
        Environment.GetEnvironmentVariable("KOIKATSU_DIR") ?? @"C:\Koikatsu";

    static Dictionary<string, string> AsmAliases => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ac"] = Path.Combine(GameRoot, @"Koikatu_Data\Managed\Assembly-CSharp.dll"),
        ["asm"] = Path.Combine(GameRoot, @"Koikatu_Data\Managed\Assembly-CSharp.dll"),
        ["kkapi"] = Path.Combine(GameRoot, @"BepInEx\plugins\KKAPI.dll"),
        ["extsave"] = Path.Combine(GameRoot, @"BepInEx\plugins\KK_BepisPlugins\ExtensibleSaveFormat.dll"),
        ["extsavecore"] = Path.Combine(GameRoot, @"BepInEx\plugins\KK_BepisPlugins\ExtensibleSaveFormat.dll"),
        ["sideloader"] = Path.Combine(GameRoot, @"BepInEx\plugins\KK_BepisPlugins\Sideloader.dll"),
        ["mat"] = Path.Combine(GameRoot, @"BepInEx\plugins\API_Plugins\MaterialEditor.dll"),
        ["matcore"] = Path.Combine(GameRoot, @"BepInEx\plugins\API_Plugins\MaterialEditor.dll"),
        ["chalphamask"] = Path.Combine(GameRoot, @"BepInEx\plugins\KK_ChaAlphaMask.dll"),
        ["shaderfixer"] = Path.Combine(GameRoot, @"BepInEx\plugins\Starstorm\ShaderFixer.Koikatu.dll"),
        ["shadermgmt"] = Path.Combine(GameRoot, @"BepInEx\plugins\ShaderManagementTool.dll"),
        ["masse"] = Path.Combine(GameRoot, @"BepInEx\plugins\Starstorm\MassShaderEditor.Koikatu.dll"),
        ["resredirect"] = Path.Combine(GameRoot, @"BepInEx\plugins\XUnity.ResourceRedirector\XUnity.ResourceRedirector.dll"),
        ["charalistopt"] = Path.Combine(GameRoot, @"BepInEx\plugins\IllusionFixes\KKP_Fix_CharacterListOptimizations.dll"),
        ["xua"] = Path.Combine(GameRoot, @"BepInEx\plugins\XUnity.AutoTranslator\XUnity.AutoTranslator.Plugin.Core.dll"),
        ["hookui"] = Path.Combine(GameRoot, @"BepInEx\plugins\KKAPI.dll"),
        ["unityengine"] = Path.Combine(GameRoot, @"Koikatu_Data\Managed\UnityEngine.dll"),
        ["unitycore"] = Path.Combine(GameRoot, @"Koikatu_Data\Managed\UnityEngine.CoreModule.dll"),
        ["0harmony"] = Path.Combine(GameRoot, @"BepInEx\core\0Harmony.dll"),
        ["bepinex"] = Path.Combine(GameRoot, @"BepInEx\core\BepInEx.dll"),
    };

    static string ResolveAssembly(string arg)
    {
        if (AsmAliases.TryGetValue(arg, out var p)) return p;
        return arg;
    }

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: ilscan <asm> <cmd> [args...]");
            Console.WriteLine("cmds: types <regex> | methods <typeRegex> | find <methodRegex> | callers <methodRegex> | callees <methodRegex> | body <methodRegex> | iface | attrs <typeRegex> | nested <typeRegex>");
            return 2;
        }

        string asmPath = ResolveAssembly(args[0]);
        if (!File.Exists(asmPath)) { Console.Error.WriteLine("MISSING ASSEMBLY: " + asmPath); return 3; }

        var rp = new ReaderParameters
        {
            ReadSymbols = false,
            ReadingMode = ReadingMode.Deferred,
            AssemblyResolver = new LocalResolver(Path.GetDirectoryName(asmPath)),
        };
        AssemblyDefinition asm;
        try { asm = AssemblyDefinition.ReadAssembly(asmPath, rp); }
        catch (Exception ex) { Console.Error.WriteLine("READ FAIL: " + ex.Message); return 4; }

        Console.WriteLine($"# asm={asm.Name.Name} {asm.Name.Version}  types={asm.MainModule.Types.Count}");
        string cmd = args[1].ToLowerInvariant();
        string pat = args.Length > 2 ? args[2] : "";
        var rx = new Regex(pat, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        try
        {
            switch (cmd)
            {
                case "types": DoTypes(asm, rx); break;
                case "methods": DoMethods(asm, rx); break;
                case "find": DoFind(asm, rx); break;
                case "callers": DoCallers(asm, rx); break;
                case "callees": DoCallees(asm, rx); break;
                case "body": DoBody(asm, rx); break;
                case "attrs": DoAttrs(asm, rx); break;
                case "patch": DoPatch(asm, rx); break;
                case "fields": DoFields(asm, rx); break;
                case "fieldrefs": DoFieldRefs(asm, rx); break;
                case "strlit": DoStrLit(asm, rx); break;
                default: Console.Error.WriteLine("unknown cmd " + cmd); return 2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("ERR: " + ex); return 5; }
        return 0;
    }

    // ---------- helpers ----------

    static IEnumerable<TypeDefinition> AllTypes(AssemblyDefinition asm)
    {
        foreach (var t in asm.MainModule.Types)
            foreach (var x in Walk(t)) yield return x;
    }
    static IEnumerable<TypeDefinition> Walk(TypeDefinition t)
    {
        yield return t;
        if (!t.HasNestedTypes) yield break;
        foreach (var n in t.NestedTypes)
            foreach (var x in Walk(n)) yield return x;
    }

    public static string Sig(MethodReference m)
    {
        var sb = new StringBuilder();
        sb.Append(TypeName(m.ReturnType)).Append(' ').Append(m.DeclaringType?.FullName).Append("::").Append(m.Name);
        if (m.HasGenericParameters)
            sb.Append('<').Append(string.Join(",", m.GenericParameters.Select(g => g.Name))).Append('>');
        sb.Append('(');
        sb.Append(string.Join(", ", m.Parameters.Select(p => TypeName(p.ParameterType) + " " + p.Name)));
        sb.Append(')');
        return sb.ToString();
    }

    public static string TypeName(TypeReference t)
    {
        if (t == null) return "?";
        if (t is GenericInstanceType g)
            return TypeName(g.ElementType) + "<" + string.Join(",", g.GenericArguments.Select(TypeName)) + ">";
        if (t is ArrayType a) return TypeName(a.ElementType) + "[]";
        if (t is ByReferenceType b) return "ref " + TypeName(b.ElementType);
        if (t is PointerType pt) return TypeName(pt.ElementType) + "*";
        if (t is GenericParameter gp) return gp.Name;
        return t.FullName;
    }

    static void DoTypes(AssemblyDefinition asm, Regex rx)
    {
        foreach (var t in AllTypes(asm))
        {
            string full = t.FullName;
            if (!rx.IsMatch(full)) continue;
            Console.WriteLine($"{full}  [base={t.BaseType?.FullName}]  attrs={(t.IsPublic ? "public" : "internal")}{(t.IsValueType ? " struct" : t.IsInterface ? " interface" : " class")}{(t.IsAbstract && t.IsSealed ? " static" : "")}");
        }
    }

    static void DoMethods(AssemblyDefinition asm, Regex rx)
    {
        foreach (var t in AllTypes(asm))
        {
            if (!rx.IsMatch(t.FullName)) continue;
            Console.WriteLine("== " + t.FullName + "  [base=" + t.BaseType?.FullName + "]");
            foreach (var m in t.Methods)
            {
                string mods = (m.IsPublic ? "public " : m.IsPrivate ? "private " : m.IsFamily ? "protected " : "internal ")
                            + (m.IsStatic ? "static " : "") + (m.IsVirtual ? "virtual " : "");
                Console.WriteLine("   " + mods + Sig(m));
            }
        }
    }

    static void DoFind(AssemblyDefinition asm, Regex rx)
    {
        foreach (var t in AllTypes(asm))
            foreach (var m in t.Methods)
            {
                string s = Sig(m);
                if (rx.IsMatch(m.Name) || rx.IsMatch(s))
                    Console.WriteLine(s);
            }
    }

    static IEnumerable<(MethodDefinition md, Instruction ins)> AllCalls(AssemblyDefinition asm)
    {
        foreach (var t in AllTypes(asm))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj)
                        yield return (m, ins);
                }
            }
    }

    static void DoCallers(AssemblyDefinition asm, Regex rx)
    {
        int n = 0;
        foreach (var (md, ins) in AllCalls(asm))
        {
            if (ins.Operand is not MethodReference mr) continue;
            string target = Sig(mr);
            if (!(rx.IsMatch(mr.Name) || rx.IsMatch(target))) continue;
            n++;
            Console.WriteLine($"{md.DeclaringType.FullName}::{md.Name}  ->  {target}   @IL_{ins.Offset:X4}");
        }
        Console.WriteLine($"# callers={n}");
    }

    static void DoCallees(AssemblyDefinition asm, Regex rx)
    {
        int n = 0;
        foreach (var t in AllTypes(asm))
            foreach (var m in t.Methods)
            {
                string self = Sig(m);
                if (!(rx.IsMatch(m.Name) || rx.IsMatch(self))) continue;
                Console.WriteLine("== " + self + (m.HasBody ? "" : "  (no body)"));
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj)
                    {
                        if (ins.Operand is MethodReference mr)
                        {
                            n++;
                            Console.WriteLine($"    IL_{ins.Offset:X4} {ins.OpCode.Name} {Sig(mr)}");
                        }
                    }
                }
            }
        Console.WriteLine($"# callees={n}");
    }

    static void DoBody(AssemblyDefinition asm, Regex rx)
    {
        foreach (var t in AllTypes(asm))
            foreach (var m in t.Methods)
            {
                string self = Sig(m);
                if (!(rx.IsMatch(m.Name) || rx.IsMatch(self))) continue;
                Console.WriteLine("===== " + self);
                if (!m.HasBody) { Console.WriteLine("  (no body)"); continue; }
                var b = m.Body;
                foreach (var v in b.Variables) Console.WriteLine($"  local V_{v.Index}: {TypeName(v.VariableType)}");
                foreach (var ins in b.Instructions)
                {
                    string op = ins.Operand switch
                    {
                        null => "",
                        string s => "\"" + s + "\"",
                        MethodReference mr => Sig(mr),
                        FieldReference fr => TypeName(fr.DeclaringType) + "::" + fr.Name,
                        TypeReference tr => TypeName(tr),
                        VariableDefinition vd => "V_" + vd.Index,
                        ParameterDefinition pd => "P_" + pd.Name,
                        Instruction it => "IL_" + it.Offset.ToString("X4"),
                        _ => ins.Operand.ToString()
                    };
                    Console.WriteLine($"  IL_{ins.Offset:X4}: {ins.OpCode.Name,-12} {op}");
                }
            }
    }

    // Find all ldstr literals matching regex, with owning method
    static void DoStrLit(AssemblyDefinition asm, Regex rx)
    {
        int n = 0;
        foreach (var t in AllTypes(asm))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode.Code != Code.Ldstr) continue;
                    var s = ins.Operand as string;
                    if (s == null || !rx.IsMatch(s)) continue;
                    Console.WriteLine($"  \"{s}\"  <-  {Sig(m)}  @IL_{ins.Offset:X4}");
                    n++;
                }
            }
        Console.WriteLine($"# matches={n}");
    }

    static void DoAttrs(AssemblyDefinition asm, Regex rx)
    {
        foreach (var t in AllTypes(asm))
        {
            if (!rx.IsMatch(t.FullName)) continue;
            Console.WriteLine("== " + t.FullName);
            foreach (var a in t.CustomAttributes) Console.WriteLine("   [" + a.AttributeType.FullName + "]");
            foreach (var m in t.Methods)
                foreach (var a in m.CustomAttributes)
                    Console.WriteLine("   " + m.Name + " [" + a.AttributeType.FullName + "]");
        }
    }

    // Dump HarmonyPatch attribute ARGUMENTS (the real patch target)
    static void DoPatch(AssemblyDefinition asm, Regex rx)
    {
        foreach (var t in AllTypes(asm))
        {
            foreach (var m in t.Methods)
            {
                string kind = null, targetType = null, targetMethod = null, methodType = null;
                var argTypes = new List<string>();
                bool sawPatch = false;
                foreach (var a in m.CustomAttributes)
                {
                    string an = a.AttributeType.FullName;
                    if (an == "HarmonyLib.HarmonyPrefix") kind = "PREFIX";
                    else if (an == "HarmonyLib.HarmonyPostfix") kind = "POSTFIX";
                    else if (an == "HarmonyLib.HarmonyTranspiler") kind = "TRANSPILER";
                    else if (an == "HarmonyLib.HarmonyPatch")
                    {
                        sawPatch = true;
                        foreach (var ca in a.ConstructorArguments)
                        {
                            if (ca.Value is TypeReference tr) targetType = tr.FullName;
                            else if (ca.Value is string s && targetMethod == null) targetMethod = s;
                            else if (ca.Value is CustomAttributeArgument[] arr)
                            {
                                foreach (var e in arr)
                                    if (e.Value is TypeReference et) argTypes.Add(et.FullName);
                            }
                        }
                        foreach (var na in a.Properties)
                        {
                            string v = na.Argument.Value as string;
                            if (na.Name == "MethodName") targetMethod = v;
                            else if (na.Name == "MethodType") methodType = v;
                        }
                    }
                }
                if (kind == null || !sawPatch) continue;
                string line = $"{kind,-11} {t.FullName}::{m.Name}  ->  {targetType}::{targetMethod}"
                            + (argTypes.Count > 0 ? "(" + string.Join(", ", argTypes) + ")" : "")
                            + (methodType != null ? $"  [{methodType}]" : "");
                if (!rx.IsMatch(t.FullName + "::" + m.Name) && !rx.IsMatch(targetType ?? "") && !rx.IsMatch(targetMethod ?? "")) continue;
                Console.WriteLine(line);
            }
        }
    }

    static void DoFields(AssemblyDefinition asm, Regex rx)
    {
        foreach (var t in AllTypes(asm))
        {
            var hits = t.Fields.Where(f => rx.IsMatch(f.Name) || rx.IsMatch(t.FullName + "::" + f.Name)).ToList();
            if (hits.Count == 0) continue;
            Console.WriteLine("== " + t.FullName);
            foreach (var f in hits)
                Console.WriteLine($"   {(f.IsStatic ? "static " : "")}{TypeName(f.FieldType)} {f.Name}");
        }
    }

    static void DoFieldRefs(AssemblyDefinition asm, Regex rx)
    {
        int n = 0;
        foreach (var t in AllTypes(asm))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.Operand is not FieldReference fr) continue;
                    string s = TypeName(fr.DeclaringType) + "::" + fr.Name;
                    if (!(rx.IsMatch(fr.Name) || rx.IsMatch(s))) continue;
                    n++;
                    Console.WriteLine($"{t.FullName}::{m.Name}  ->  {s}   @IL_{ins.Offset:X4}  ({ins.OpCode.Name})");
                }
            }
        Console.WriteLine($"# refs={n}");
    }
}

class LocalResolver : IAssemblyResolver
{
    readonly string _dir;
    readonly Dictionary<string, AssemblyDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);
    public LocalResolver(string dir) { _dir = dir; }
    public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_cache.TryGetValue(name.Name, out var a)) return a;
        string p = Path.Combine(_dir, name.Name + ".dll");
        if (!File.Exists(p)) return null;
        var rp = new ReaderParameters { ReadSymbols = false, ReadingMode = ReadingMode.Deferred, AssemblyResolver = this };
        a = AssemblyDefinition.ReadAssembly(p, rp);
        _cache[name.Name] = a;
        return a;
    }
    public void Dispose() { }
}
