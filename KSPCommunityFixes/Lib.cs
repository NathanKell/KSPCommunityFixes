using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KSPCommunityFixes
{
    public static class Lib
    {
        public static bool EditPartModuleKSPFieldAttributes(Type partModuleType, string fieldName, Action<KSPField> editAction)
        {
            BaseFieldList<BaseField, KSPField>.ReflectedData reflectedData;
            try
            {
                MethodInfo BaseFieldList_GetReflectedAttributes = AccessTools.Method(typeof(BaseFieldList), "GetReflectedAttributes");
                reflectedData = (BaseFieldList<BaseField, KSPField>.ReflectedData)BaseFieldList_GetReflectedAttributes.Invoke(null, new object[] { partModuleType, false });
            }
            catch
            {
                return false;
            }

            for (int i = 0; i < reflectedData.fields.Count; i++)
            {
                if (reflectedData.fields[i].Name == fieldName)
                {
                    editAction.Invoke(reflectedData.fieldAttributes[i]);
                    return true;
                }
            }

            return false;
        }
    }
}
