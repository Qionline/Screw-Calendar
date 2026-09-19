using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScrewCalendar;

public static class Localization
{
    private static readonly JsonSerializerOptions Options = new();
    private static Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static CalendarLanguage Current { get; private set; } = CalendarLanguage.ChineseSimplified;

    public static void SetLanguage(CalendarLanguage language)
    {
        Current = language;
        var file = language == CalendarLanguage.English ? "en-US.json" : "zh-CN.json";
        var path = Path.Combine(AppContext.BaseDirectory, "locales", file);
        try
        {
            var values = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Options) : null;
            _values = values is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(values, StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Failed to load localization file: {path}", exception);
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public static string T(string key, params object[] args)
    {
        var value = _values.TryGetValue(key, out var localized) ? localized : key;
        return args.Length == 0 ? value : string.Format(value, args);
    }

    public static string Holiday(string name) => T(name switch
    {
        "元旦" => "holiday.newYear",
        "春节" => "holiday.springFestival",
        "清明节" => "holiday.tombSweeping",
        "劳动节" => "holiday.labour",
        "端午节" => "holiday.dragonBoat",
        "中秋节" => "holiday.midAutumn",
        "国庆节" => "holiday.national",
        "国庆节、中秋节" => "holiday.nationalMidAutumn",
        _ => name
    });

    public static string Term(string name) => T(name switch
    {
        "小寒" => "term.xiaohan",
        "大寒" => "term.dahan",
        "立春" => "term.lichun",
        "雨水" => "term.yushui",
        "惊蛰" => "term.jingzhe",
        "春分" => "term.chunfen",
        "清明" => "term.qingming",
        "谷雨" => "term.guyu",
        "立夏" => "term.lixia",
        "小满" => "term.xiaoman",
        "芒种" => "term.mangzhong",
        "夏至" => "term.xiazhi",
        "小暑" => "term.xiaoshu",
        "大暑" => "term.dashu",
        "立秋" => "term.liqiu",
        "处暑" => "term.chushu",
        "白露" => "term.bailu",
        "秋分" => "term.qiufen",
        "寒露" => "term.hanlu",
        "霜降" => "term.shuangjiang",
        "立冬" => "term.lidong",
        "小雪" => "term.xiaoxue",
        "大雪" => "term.daxue",
        "冬至" => "term.dongzhi",
        _ => name
    });
}
