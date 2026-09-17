using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

public static class PlotWorkflowProbe
{
    private static Type configType, workflowType, entryType, kindType, formatType;
    private static readonly BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static object Get(object obj, string property) { return obj.GetType().GetProperty(property, Instance).GetValue(obj, null); }
    private static void Set(object obj, string property, object value) { obj.GetType().GetProperty(property, Instance).SetValue(obj, value, null); }
    private static object Call(object obj, string method, params object[] args)
    {
        try { return obj.GetType().GetMethod(method, Instance).Invoke(obj, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static object Config(string format)
    {
        var config = Activator.CreateInstance(configType, true);
        Set(config, "SourcePort", "COM91"); Set(config, "Format", Enum.Parse(formatType, format));
        return config;
    }
    private static object Workflow(object config) { return Activator.CreateInstance(workflowType, Instance, null, new[] { config }, null); }
    private static void Feed(object workflow, string text, string connection = "uart:COM91:1", string port = "COM91")
    { FeedBytes(workflow, Encoding.UTF8.GetBytes(text), connection, port); }
    private static void FeedBytes(object workflow, byte[] bytes, string connection, string port)
    {
        var entry = Activator.CreateInstance(entryType, Instance, null,
            new object[] { 1L, DateTime.Now, port, connection, Enum.Parse(kindType, "Rx"), bytes, "", 65001 }, null);
        Call(workflow, "Process", entry);
    }
    private static object Snapshot(object workflow) { return Call(workflow, "Snapshot"); }
    private static object[] Series(object workflow) { return ((IEnumerable)Get(Snapshot(workflow), "Series")).Cast<object>().ToArray(); }
    private static object[] Samples(object series) { return ((IEnumerable)Get(series, "Samples")).Cast<object>().ToArray(); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static string[] Run(Assembly assembly, string root)
    {
        configType=assembly.GetType("llcom_plus.Tools.PlotConfiguration",true);
        workflowType=assembly.GetType("llcom_plus.Tools.PlotWorkflow",true);
        entryType=assembly.GetType("llcom_plus.Tools.SerialTraceEntry",true);
        kindType=assembly.GetType("llcom_plus.Tools.SerialTraceKind",true);
        formatType=assembly.GetType("llcom_plus.Tools.PlotInputFormat",true);
        var passed=new List<string>();
        var single=Workflow(Config("Single"));
        Assert(Series(single).Length==0, "A new plot must not fabricate a zero series.");
        Feed(single,"12.");
        Assert((int)Get(Snapshot(single),"PendingCharacters")==3 && Series(single).Length==0,"Partial lines must wait.");
        Feed(single,"5\n");
        Assert((double)Get(Samples(Series(single)[0])[0],"Value")==12.5,"Split numeric frame corrupted.");
        Feed(single,"900\n","uart:COM92:1","COM92");
        Assert((long)Get(Snapshot(single),"AcceptedLines")==1,"Wrong COM entered plot.");
        passed.Add("Empty plots stay empty; partial lines combine only within the selected COM.");
        Feed(single,"NaN\n");
        Assert((long)Get(Snapshot(single),"RejectedLines")==1,"Non-finite sample was accepted.");
        Feed(single,"1"); Feed(single,"2\n","uart:COM91:2");
        Assert(Samples(Series(single)[0]).Length==1 && (double)Get(Samples(Series(single)[0])[0],"Value")==2,
            "Reconnection must not combine old partial bytes or old samples.");
        passed.Add("Invalid numbers are rejected; connection changes reset incomplete frames and stale samples.");
        var csv=Workflow(Config("Csv")); Feed(csv,"1,2,3\r\n");
        Assert(Series(csv).Length==3 && (long)Get(Snapshot(csv),"AcceptedLines")==1,"CSV/CRLF parsing failed.");
        var kv=Workflow(Config("KeyValue")); Feed(kv,"temp=23.5,voltage=3.7\n");
        Assert(Series(kv).Length==2,"key=value parsing failed.");
        var json=Workflow(Config("Json"));
        var raw=Encoding.UTF8.GetBytes("{\"温度\":23.5,\"voltage\":3.7}\n");
        FeedBytes(json,raw.Take(4).ToArray(),"uart:COM91:1","COM91");
        FeedBytes(json,raw.Skip(4).ToArray(),"uart:COM91:1","COM91");
        Assert(Series(json).Length==2 && (string)Get(Get(Series(json)[0],"Options"),"Key")=="温度", "UTF8 split/JSON parsing failed.");
        passed.Add("CSV, key=value and JSON parse correctly, including split multi-byte UTF8.");
        var regexConfig=Config("Regex"); Set(regexConfig,"Pattern",@"temp=(?<temp>\d+(?:\.\d+)?)");
        var regex=Workflow(regexConfig); Feed(regex,"temp=42.5\n");
        Assert((double)Get(Samples(Series(regex)[0])[0],"Value")==42.5,"Named regex capture failed.");
        var bounded=Workflow(Config("Single")); Feed(bounded,new string('1',9000)+"\n42\n");
        Assert((long)Get(Snapshot(bounded),"RejectedLines")==1 && (long)Get(Snapshot(bounded),"AcceptedLines")==1,
            "Oversized line must be discarded through its terminator.");
        passed.Add("Named regex captures work and oversized frames are skipped without using their tail.");
        for(int i=0;i<5010;i++) Feed(bounded,"2\n");
        Assert(Samples(Series(bounded)[0]).Length==5000,"Chart retention must remain bounded.");
        var options=Get(Series(bounded)[0],"Options"); Set(options,"Name","=formula"); Set(options,"Unit","V"); Set(options,"Visible",false);
        var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(options.GetType())); list.Add(options);
        Call(bounded,"UpdateSeriesOptions",list);
        Assert(Samples(Series(bounded)[0]).Length==5000 && !(bool)Get(Get(Series(bounded)[0],"Options"),"Visible"),"Style changes must preserve samples.");
        var exported=(string)workflowType.GetMethod("ToCsv",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{Snapshot(bounded)});
        Assert(exported.Contains("'=") && exported.Contains("V"),"CSV must retain units and neutralize formulas.");
        Call(bounded,"Clear"); Assert(Samples(Series(bounded)[0]).Length==0,"Clear must remove only chart samples.");
        passed.Add("Retention, label/unit/visibility changes, safe CSV and clear preserve their contracts.");
        var script=Workflow(Config("Script")); Feed(script,"15\n");
        Assert(Series(script).Length==0,"Script mode must not parse serial values.");
        Call(script,"AddScriptPoint",15d,1,DateTime.Now);
        Assert((string)Get(Get(Series(script)[0],"Options"),"Key")=="2","Legacy zero-based script line mapping changed.");
        passed.Add("Explicit script mode preserves apiAddPoint line numbering without mixing serial parsing.");
        var store=assembly.GetType("llcom_plus.Tools.PlotSettingsStore",true);
        var path=Path.Combine(root,"plot-settings.json"); File.WriteAllText(path,"invalid-json");
        var args=new object[]{root,null};
        store.GetMethod("Load",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
        Assert(!string.IsNullOrEmpty((string)args[1]) && File.ReadAllText(path)=="invalid-json","Invalid settings must be preserved.");
        store.GetMethod("Save",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{(object)root,Config("Single")});
        Assert(File.ReadAllText(path+".invalid")=="invalid-json","Saving after recovery must keep old corrupt data.");
        passed.Add("Invalid plot configuration is preserved before a replacement is saved atomically.");
        return passed.ToArray();
    }
}
