using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PulseVideoTool
{
    internal sealed class ToolSettings
    {
        public string InputDirectory { get; set; }
        public string OutputDirectory { get; set; }
        public bool Recurse { get; set; }
        public bool Alpha { get; set; }
        public int FrameRate { get; set; }
        public int CRF { get; set; }
        public int GOP { get; set; }
        public string Preset { get; set; }

        /// <summary>创建默认转码参数，供配置反序列化和首次启动使用。</summary>
        public ToolSettings()
        {
            InputDirectory = "";
            OutputDirectory = "";
            FrameRate = 30;
            CRF = 18;
            GOP = 60;
            Preset = "veryfast";
        }

        internal static ToolSettings Load()
        {
            if (!File.Exists(ToolPaths.SettingsPath)) return new ToolSettings();
            return new JavaScriptSerializer().Deserialize<ToolSettings>(File.ReadAllText(ToolPaths.SettingsPath))
                ?? new ToolSettings();
        }

        internal void Save()
        {
            Directory.CreateDirectory(ToolPaths.UserDirectory);
            string temporary = ToolPaths.SettingsPath + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(ToolPaths.SettingsPath)) File.Replace(temporary, ToolPaths.SettingsPath, null);
            else File.Move(temporary, ToolPaths.SettingsPath);
        }
    }
}
