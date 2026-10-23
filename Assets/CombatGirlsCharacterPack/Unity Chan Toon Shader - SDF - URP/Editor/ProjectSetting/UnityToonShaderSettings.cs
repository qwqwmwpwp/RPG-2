using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEditorInternal;
namespace UnityEditor.Rendering.Toon
{
    public class UnityToonShaderSettings : ScriptableObject
    {
        const string kFilePath = "ProjectSettings/UnityToonShaderSettings.asset";
        static UnityToonShaderSettings s_Instance;

        public static UnityToonShaderSettings instance => s_Instance ?? CreateOrLoad();

        [SerializeField]
        public bool m_ShowConverter = true;
        [SerializeField]
        public bool m_ShowDepracated = false;
        UnityToonShaderSettings()
        {
            s_Instance = this;
        }

        static internal void Save()
        {

            string folderPath = Path.GetDirectoryName(kFilePath);
            if (!Directory.Exists(folderPath))
                Directory.CreateDirectory(folderPath);

            InternalEditorUtility.SaveToSerializedFileAndForget(new[] { s_Instance }, kFilePath, allowTextSerialization: true);
        }

        static internal UnityToonShaderSettings CreateOrLoad()
        {
            InternalEditorUtility.LoadSerializedFileAndForget(kFilePath);

            //else create
            if (s_Instance == null)
            {
                UnityToonShaderSettings created = CreateInstance<UnityToonShaderSettings>();
                created.hideFlags = HideFlags.HideAndDontSave;
            }

            return s_Instance;

        }
    }
}

