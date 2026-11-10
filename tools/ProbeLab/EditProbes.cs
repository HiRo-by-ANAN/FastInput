using ProbeLab.Model;
using System.Text.Json;

namespace ProbeLab;
public static class EditProbes
{
    const string Sample="A😀é👨‍👩‍👧‍👦🇹🇼B";
    static void Reset(FixtureSession f,string id,string text,int start=0,int length=0)
    { f.Send(new{op="text",id,text});f.Send(new{op="select",id,start,length}); }
    static string Text(FixtureSession f,string id)=>f.Control(id).GetProperty("text").GetString()!;
    static int Start(FixtureSession f,string id)=>f.Control(id).GetProperty("start").GetInt32();
    static Native.INPUT[] Press(ushort vk)=>Native.Key(vk).Concat(Native.Key(vk,true)).ToArray();
    public static void Replace(Report r)
    {
        using var f=FixtureSession.Start(r);
        foreach(string id in new[]{"single","multi","rich"})
        {
            if(!f.Focus(id))continue;
            Reset(f,id,"Goagle");var h=f.Handle(id);
            f.Target.Message(h,0xB1,2,3);f.Target.Text(h,0xC2,"o");
            f.Capture(id+" EM_REPLACESEL",Text(f,id)=="Google","Goagle 在 UTF-16 [2,3) 改成 o，全文 Google；事件log含selection與CanUndo");
            bool canUndo=f.Control(id).GetProperty("canUndo").GetBoolean();
            f.Target.Message(h,0xC7,0,0);f.Capture(id+" EM_UNDO",canUndo && Text(f,id)=="Goagle","CanUndo=true 且單次 EM_UNDO 回到 Goagle");
            Reset(f,id,"Goagle",2,1);f.Target.Text(h,0xC,"Google");f.Capture(id+" WM_SETTEXT對照",Text(f,id)=="Google","全文 Google；比較selection/CanUndo/事件序列");
        }
        if(!f.Focus("single"))return;
        var target=f.Handle("single");
        foreach(var race in new[]{"select","text"})
        {
            Reset(f,"single","Goagle");f.Target.Message(target,0xB1,2,3);var expected=f.Control("single");
            if(race=="select") f.Send(new{op="select",id="single",start=0,length=1});
            else f.Send(new{op="text",id="single",text="EXTERNAL_FIXTURE_CHANGE"});
            var observed=f.Control("single");bool detected=expected.GetRawText()!=observed.GetRawText();
            r.Add("E7 SETSEL後偏離 "+race,new{expected,observed,detected,replacementSuppressed=detected},detected,"比較fixture真值可偵測偏離；偵測到便不執行REPLACESEL。這不是原子交易");
            if(!detected) f.Target.Text(target,0xC2,"o");
        }
        Reset(f,"single","Goagle");f.Target.Message(target,0xB1,2,3);
        f.Send(new{op="block",id="single",ms=1200});
        bool completed=f.Target.Text(target,0xC2,"o",100);
        // Never retry an uncertain write; wait for the target to recover and read fixture truth.
        Thread.Sleep(1400);f.Send(new{op="snapshot"});
        r.Add("E8 timeout後是否晚完成",new{timeoutMs=100,blockMs=1200,completed,lateCompleted=Text(f,"single")=="Google",lateText=Text(f,"single"),state=f.Snapshot(),events=f.ReadEvents()},!completed,"已進入1200ms UI阻塞時100ms等待應逾時；另觀察之後文字是否仍成Google。timeout不代表取消，不重送");
        if(!f.Focus("decoy")){r.Add("E1 decoy實驗未執行",new{reason="decoy未取得焦點"});return;}string decoy=Text(f,"decoy");
        f.Target.Message(target,0xB1,0,1);f.Target.Text(target,0xC2,"X");
        f.Capture("E1 decoy前景 HWND定向訊息",Text(f,"decoy")==decoy,"decoy文字不變；兩窗皆fixture同PID，訊息仍只送指定欄位HWND");
        // An unowned HWND must be rejected without reading its text or sending anything.
        bool refused=!f.Target.Guard(0,"invalid HWND dry denial");r.Add("安全拒絕空HWND",new{refused},refused,"無效／非擁有 HWND 必須拒絕");
    }
    public static void Unicode(Report r,bool modelOnly)
    {
        var boundaries=TextModel.Graphemes(Sample);
        r.Add("E4 純模型",new{model=TextModel.Describe(Sample),graphemeCount=boundaries.Length-1,familyCluster=TextModel.Graphemes("👨‍👩‍👧‍👦").Length-1,flagCluster=TextModel.Graphemes("🇹🇼").Length-1,combining=TextModel.Describe("e\u0301"),runtime=Environment.Version.ToString()},boundaries.First()==0 && boundaries.Last()==Sample.Length,"邊界涵蓋全文；家庭與旗幟分群依目前.NET實測值，不預設Windows控制項相同");
        if(modelOnly){r.Add("E4 桌面實驗未執行",new{reason="--model-only：未建立視窗或呼叫Win32"},null,"此結果僅涵蓋模型；控制項移動／刪除行為仍待真實桌面量測");return;}
        using var f=FixtureSession.Start(r);
        foreach(var id in new[]{"single","multi","rich"})
        {
            if(!f.Focus(id))continue;var hwnd=f.Handle(id);
            Reset(f,id,"");f.Target.Text(hwnd,0xC2,Sample);f.Capture("E4 "+id+" Unicode插入",Text(f,id)==Sample,"完整UTF-16相同");
            // Explicitly test every UTF-16 offset, including surrogate/ZWJ interiors.
            for(int p=0;p<=Sample.Length;p++)
            {
                f.Target.Message(hwnd,0xB1,p,p);f.Send(new{op="snapshot"});r.Add($"E4 {id} SETSEL {p}",new{requested=p,actual=f.Control(id)});
            }
            foreach(var (name,key,initial) in new[]{("Right",(ushort)0x27,0),("Left",(ushort)0x25,Sample.Length)})
            {
                Reset(f,id,Sample,initial);
                for(int i=0;i<Sample.Length+2;i++)
                {
                    int before=Start(f,id);f.Target.Input(hwnd,Press(key),"E4 "+name);Thread.Sleep(35);f.Send(new{op="snapshot"});int after=Start(f,id);
                    r.Add($"E4 {id} {name} #{i}",new{before,after,deltaUtf16=after-before,state=f.Control(id)});
                    if(before==after)break;
                }
            }
            foreach(var (name,key) in new[]{("Backspace",(ushort)8),("Delete",(ushort)0x2E)})
            {
                Reset(f,id,Sample,name=="Backspace"?Sample.Length:0);
                for(int i=0;i<Sample.Length+2;i++)
                {
                    string before=Text(f,id);var selection=f.Control(id);f.Target.Input(hwnd,Press(key),"E4 "+name);Thread.Sleep(35);f.Send(new{op="snapshot"});string after=Text(f,id);
                    r.Add($"E4 {id} {name} #{i}",new{before,after,deletedUtf16=before.Length-after.Length,beforeSelection=selection,afterSelection=f.Control(id)});
                    if(before==after)break;
                }
            }
            f.Capture("E4 "+id+" 完整事件");
        }
    }
    public static void SendInput(Report r,int trials)
    {
        using var f=FixtureSession.Start(r);
        foreach(var id in new[]{"single","multi"})
        foreach(var text in new[]{"abc123","中文","😀","\n","\r","\t"})
        {
            if(!f.Focus(id))continue;Reset(f,id,"");
            int first=f.ReadEvents().Length;
            uint sent=f.Target.Input(f.Handle(id),Native.Unicode(text),"E5 unicode");Thread.Sleep(150);f.Send(new{op="snapshot"});
            r.Add($"E5/E6/E14 {id} UTF16 "+string.Join(",",text.Select(c=>$"{(int)c:X4}")),new{text,requestedEvents=text.Length*2,sent,final=f.Snapshot(),events=f.ReadEvents().Skip(first).ToArray()},text is "\n" or "\r" or "\t"?null:Text(f,id)==text,"一般文字完整相等；三個控制字元只觀察WM_CHAR/KeyPress/focus及文字，不假設等同虛擬Enter/Tab");
        }
        if(!f.Focus("single"))return;
        Reset(f,"single","Goagle",3);
        var replace=Native.Key(0x10).Concat(Press(0x25)).Concat(Native.Key(0x10,true)).Concat(Native.Unicode("o")).ToArray();
        f.Target.Input(f.Handle("single"),replace,"E5 Shift Left + o");Thread.Sleep(150);f.Send(new{op="snapshot"});f.Capture("E5 最小鍵盤替換",Text(f,"single")=="Google","Goagle UTF-16游標3 Shift+Left選a再輸入o，結果Google");
        int decoyHits=0,singleHits=0,denied=0,unknownOrOther=0;
        for(int i=0;i<trials;i++)
        {
            if(!f.Focus("single")) {denied++;continue;}
            Reset(f,"single","");Reset(f,"decoy","");
            bool precheck=f.Target.Guard(f.Handle("single"),"E5 race前置檢查");
            bool switched=f.Focus("decoy");
            if(!switched){denied++;r.Add("E5 競態切換未執行",new{trial=i,precheck,switched});continue;}
            uint sent=precheck?f.Target.Input(f.Handle("single"),Native.Unicode("R"),"E5 race最終檢查與送入"):0;
            Thread.Sleep(80);f.Send(new{op="snapshot"});
            bool hitDecoy=Text(f,"decoy").Contains('R'),hitSingle=Text(f,"single").Contains('R');
            if(hitDecoy)decoyHits++;if(hitSingle)singleHits++;if(sent==0)denied++;
            if(sent>0 && !hitDecoy && !hitSingle)unknownOrOther++;
            r.Add("E5 同PID跨視窗競態 #"+i,new{precheck,switched,sent,state=f.Snapshot()},null,"在前置檢查後切到自有decoy；觀察PID檢查不能保證原欄位落點，從不切到外部視窗");
        }
        r.Add("E5 競態落點統計",new{trials,decoyHits,singleHits,denied,unknownOrOther},null,"此實驗揭露同PID不同HWND/focus的風險；非宣稱SendInput具原子性");
    }
}
