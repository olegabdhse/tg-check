using System.Text.RegularExpressions;

namespace TgCheck.Core.Services;

/// <summary>
/// Язык блока кода по его содержимому (когда автор не указал): быстрые характерные признаки, без разбора кода.
/// null — не похоже ни на один известный язык.
/// </summary>
public static partial class CodeLanguage
{
    public static string? Detect(string code)
    {
        var t = code.Trim();
        if (t.Length == 0) return null;

        if (t.StartsWith("<?php")) return "php";
        if ((t[0] is '{' or '[') && (t[^1] is '}' or ']') && t.Contains('"') && t.Contains(':') && !t.Contains(';')) return "json";
        if (t[0] == '<' && (t.Contains("</") || t.Contains("/>")))
            return HtmlRx().IsMatch(t) ? "html" : "xml";
        if (SqlRx().IsMatch(t)) return "sql";
        if (t.Contains("#include") || t.Contains("std::") || t.Contains("cout <<")) return "cpp";
        if (t.Contains("package main") || (t.Contains("func ") && t.Contains(":="))) return "go";
        if (t.Contains("fn main") || t.Contains("let mut ") || t.Contains("println!")) return "rust";
        if (t.Contains("System.out.print") || t.Contains("public static void main")) return "java";
        if (t.Contains("Console.Write") || t.Contains("Console.Read") || t.Contains("using System") || CsRx().IsMatch(t)) return "csharp";
        if (t.Contains("fun main") || t.Contains("val ") && t.Contains("fun ")) return "kotlin";
        if (PythonRx().IsMatch(t)) return "python";
        if (t.Contains("console.log") || t.Contains("document.") || t.Contains("require(") || JsRx().IsMatch(t))
            return TsRx().IsMatch(t) ? "typescript" : "javascript";
        if (PowerShellRx().IsMatch(t)) return "powershell";
        if (ShellRx().IsMatch(t)) return "shell";
        if (CssRx().IsMatch(t)) return "css";
        if (YamlRx().IsMatch(t)) return "yaml";
        return null;
    }

    [GeneratedRegex(@"<(!DOCTYPE html|html|head|body|div|span|p|a|script|style)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlRx();

    [GeneratedRegex(@"^\s*(SELECT\s.+\sFROM|INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM|CREATE\s+(TABLE|DATABASE|INDEX|VIEW)|ALTER\s+TABLE|DROP\s+(TABLE|DATABASE))\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SqlRx();

    [GeneratedRegex(@"\b(namespace\s+\w|public\s+(static\s+)?(class|void|async|record|interface)\b|var\s+\w+\s*=.*;|\basync\s+Task\b|string\[\]|\$""|=>\s*[^;]*;)")]
    private static partial Regex CsRx();

    [GeneratedRegex(@"(^|\n)\s*(def\s+\w+\s*\(|import\s+\w+\s*($|\n)|from\s+\w+(\.\w+)*\s+import\s|print\(|if\s+__name__\s*==|elif\s|class\s+\w+(\(.*\))?:)")]
    private static partial Regex PythonRx();

    [GeneratedRegex(@"(^|\n)\s*(const|let|function|export|import\s.+\sfrom)\s|=>\s*\{")]
    private static partial Regex JsRx();

    [GeneratedRegex(@"(:\s*(string|number|boolean|any)\b|\binterface\s+\w+\s*\{|\btype\s+\w+\s*=)")]
    private static partial Regex TsRx();

    [GeneratedRegex(@"(^|\n)\s*(\$env:|Get-\w+|Set-\w+|New-\w+|Remove-\w+|Write-Host|Start-Process|Invoke-\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellRx();

    [GeneratedRegex(@"(^|\n)\s*(\$\s+)?(sudo|cd|ls|cat|echo|export|chmod|chown|mkdir|rm|cp|mv|apt(-get)?|yum|dnf|brew|pip3?|npm|npx|yarn|pnpm|dotnet|git|docker|kubectl|systemctl|journalctl|service|curl|wget|ssh|scp|tar|unzip|grep|find|nano|vim|source|bash|sh|python3?|node|go|cargo|make|ufw|crontab)(\s|$)")]
    private static partial Regex ShellRx();

    [GeneratedRegex(@"^[\w\s.#:,\[\]=""*>~+-]+\{\s*[\w-]+\s*:\s*[^;{}]+;")]
    private static partial Regex CssRx();

    [GeneratedRegex(@"^([ \t]*([\w-]+:|-\s)[^\n]*\n?){2,}$")]
    private static partial Regex YamlRx();
}
