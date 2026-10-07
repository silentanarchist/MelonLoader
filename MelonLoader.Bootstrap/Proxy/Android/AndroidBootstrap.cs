#if ANDROID
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using MelonLoader.Java;
using MelonLoader.Utils;

namespace MelonLoader.Bootstrap.Proxy.Android;

public static class AndroidBootstrap
{
    public static string PackageName;
    public static string DotnetDir;

    [RequiresDynamicCode("Calls Init then InitConfig ")]
    public static unsafe int LoadBootstrap()
    {
        // linux-bionic .NET logs everything to stdout/err, this allows us to see these logs in logcat with our logs
        StdRedirect.RedirectStdOut();
        StdRedirect.RedirectStdErr();

        Core.Init(NativeLibrary.Load("libmain.so"));
        return 1;
    }
    
    public static string GetDataDir()
    {
        JClass unityPlayer = JNI.FindClass("com/unity3d/player/UnityPlayer");
        JFieldID activityFieldId = JNI.GetStaticFieldID(unityPlayer, "currentActivity", "Landroid/app/Activity;");
        JObject currentActivityObj = JNI.GetStaticObjectField<JObject>(unityPlayer, activityFieldId);
        var callObjectMethod = JNI.CallObjectMethod<JString>(currentActivityObj, JNI.GetMethodID(JNI.GetObjectClass(currentActivityObj), "getPackageName", "()Ljava/lang/String;"));
        PackageName =  callObjectMethod.GetString();
        // JNI.DeleteLocalRef(callObjectMethod);
    
        JClass environment = JNI.FindClass("android/os/Environment");
        var getExtDir = JNI.CallStaticObjectMethod<JObject>(environment, JNI.GetStaticMethodID(environment, "getExternalStorageDirectory", "()Ljava/io/File;"));
    
        var jMethodID = JNI.GetMethodID(JNI.GetObjectClass(getExtDir), "toString", "()Ljava/lang/String;");
        var objectMethod = JNI.CallObjectMethod<JString>(getExtDir, jMethodID);
        
        return Path.Combine(objectMethod.GetString(), "MelonLoader", PackageName);
    }
    
    // TODO: crashes on older android versions because the API is not available
    //       should probably check the API level before calling this
    public static bool EnsurePerms()
    {
        const int TRIES = 3;  // Number of attempts
        const int DELAY = 5000; // Delay in milliseconds
        for (int i = 0; i < TRIES; i++)
        {
            JClass unityPlayer = JNI.FindClass("com/unity3d/player/UnityPlayer");
            JFieldID activityFieldId = JNI.GetStaticFieldID(unityPlayer, "currentActivity", "Landroid/app/Activity;");
            JObject currentActivityObj = JNI.GetStaticObjectField<JObject>(unityPlayer, activityFieldId);
    
            JClass environment = JNI.FindClass("android/os/Environment");
            JClass uri = JNI.FindClass("android/net/Uri");
            JClass intent = JNI.FindClass("android/content/Intent");
    
        
            var callStaticMethod = JNI.CallStaticMethod<bool>(environment, environment.GetStaticMethodID("isExternalStorageManager", "()Z"));
            if (JNI.ExceptionCheck())
                return false;
    
            if (callStaticMethod)
                return true;
            
            var actionName = JNI.NewString("android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION");
    
            var packageName = JNI.NewString($"package:{PackageName}");
    
            var callStaticObjectMethod = JNI.CallStaticObjectMethod<JObject>(uri, uri.GetStaticMethodID("parse", "(Ljava/lang/String;)Landroid/net/Uri;"), packageName);
            JMethodID intentConstructor = JNI.GetMethodID(intent, "<init>", "(Ljava/lang/String;Landroid/net/Uri;)V");
    
            var newObject = JNI.NewObject<JObject>(intent, intentConstructor, actionName, callStaticObjectMethod);
    
            var activityClass = JNI.GetObjectClass(currentActivityObj);
            activityClass.CallVoidMethod(currentActivityObj, "startActivity", "(Landroid/content/Intent;)V", newObject);
    
            JNI.CheckExceptionAndThrow();
            
            // TODO: this shouldn't sleep in the main thread i dont think
            Thread.Sleep(DELAY);
        }
        
        return true;
    }
    
    public static DateTimeOffset GetApkModificationDate()
    { 
        var assetBytes = APKAssetManager.GetAssetBytes("lemon_patch_date.txt");
        string assetContent = Encoding.UTF8.GetString(assetBytes);
    
        // Now parse the string content into an RFC 3339 DateTime
        // RFC 3339 is essentially ISO 8601, so DateTime.Parse can handle it.
        if (DateTimeOffset.TryParse(assetContent, out DateTimeOffset date))
            return date;
    
        return default;
    }
    
    // Warpforge Revival change.
    // The copy used to be skipped only when the target folder's "last changed" time was newer than the
    // patch date. A folder's time only changes when something is added or removed directly in it, and
    // the copy overwrites files in sub-folders that already exist - so after the first re-patch the
    // check failed for ever and everything (about 50 MB) was copied again on every start.
    // Now a small marker file records which patch the folder was copied from.
    private static bool AlreadyCopied(string folder, string stamp)
    {
        try
        {
            var marker = Path.Combine(folder, ".copied-from-patch");
            return Directory.Exists(folder) && File.Exists(marker) && File.ReadAllText(marker).Trim() == stamp;
        }
        catch { return false; }
    }

    private static void MarkCopied(string folder, string stamp)
    {
        try { File.WriteAllText(Path.Combine(folder, ".copied-from-patch"), stamp); }
        catch (Exception e) { MelonDebug.Log("Could not write the copy marker in " + folder + ": " + e.Message); }
    }

    public static void CopyMelonLoaderData(DateTimeOffset date)
    {
        var baseDataDir = Core.DataDir;
        var melonloaderFolder = Path.Combine(Core.DataDir, "MelonLoader");
        var baseInternalFolder = $"/data/data/{PackageName}/";
        var dotnetFolder = $"/data/data/{PackageName}/dotnet";
        // an unreadable patch date never matches, so the files are then copied as before
        string stamp = date == default ? "" : date.UtcDateTime.ToString("O");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        if (stamp.Length > 0 && AlreadyCopied(melonloaderFolder, stamp))
            AndroidProxy.Log("MelonLoader folder is already up-to-date");
        else
        {
            APKAssetManager.SaveItemToDirectory("MelonLoader", baseDataDir, true);
            if (stamp.Length > 0) MarkCopied(melonloaderFolder, stamp);
            AndroidProxy.Log($"MelonLoader folder copied from the app ({clock.ElapsedMilliseconds} ms)");
        }

        clock.Restart();
        if (stamp.Length > 0 && AlreadyCopied(dotnetFolder, stamp))
            AndroidProxy.Log("Dotnet folder is already up-to-date");
        else
        {
            APKAssetManager.SaveItemToDirectory("dotnet", baseInternalFolder, true);
            if (stamp.Length > 0) MarkCopied(dotnetFolder, stamp);
            AndroidProxy.Log($"Dotnet folder copied from the app ({clock.ElapsedMilliseconds} ms)");
        }

        DotnetDir = Path.Combine(dotnetFolder);
    }
}
#endif