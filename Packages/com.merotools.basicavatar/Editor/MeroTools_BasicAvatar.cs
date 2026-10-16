// MeroTools_BasicAvatar.cs  (v2)
// Unity 2022.3 LTS / VRChat Avatar 用 セットアップ拡張
// 配置先: Packages/com.merotools.basicavatar/Editor/MeroTools_BasicAvatar.cs (VPM パッケージ)
//
// ・各パッケージの型はリフレクションで解決 (未導入でもコンパイルエラーにならない)
// ・UI でチェックを付け外ししてから「セットアップ」/「削除」を実行
// ・VRCFury は SerializedProperty.managedReferenceValue で書き込み、結果を検証する

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MeroTools
{
    // ======================================================================
    // メニュー
    // ======================================================================
    public static class MeroTools_BasicAvatarMenu
    {
        [MenuItem("Tools/MeroTools/BasicAvatar Setup")]
        public static void OpenFromTools() => MeroBasicAvatarWindow.Open();

        [MenuItem("GameObject/MeroTools/Apply BasicAvatar Setup", false, 20)]
        public static void OpenFromHierarchy() => MeroBasicAvatarWindow.Open();
    }

    // ======================================================================
    // コア処理
    // ======================================================================
    public static class MeroCore
    {
        public const string AnchorName = "Anchor Object";
        public const string FloorAdjusterName = "床の位置に調整してね";
        // 旧バージョンで作った名前 (見つけたら新しい名前へ自動リネーム)
        private const string LegacyAnchorName = "[Anchor] Hips";
        private const string LegacyFloorAdjusterName = "FloorAdjuster";

        public static Transform FindAnchor(GameObject root) =>
            root.transform.Find(AnchorName) ?? root.transform.Find(LegacyAnchorName);

        public static Transform FindFloor(GameObject root) =>
            root.transform.Find(FloorAdjusterName) ?? root.transform.Find(LegacyFloorAdjusterName);

        public const int StepAnchor = 0;
        public const int StepMeshSettings = 1;
        public const int StepAAO = 2;
        public const int StepVRCFury = 3;
        public const int StepLAC = 4;
        public const int StepFloor = 5;
        public const int StepCount = 6;

        private const BindingFlags AllInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const string TMeshSettings = "nadena.dev.modular_avatar.core.ModularAvatarMeshSettings";
        private const string TFloorAdjuster = "nadena.dev.modular_avatar.core.ModularAvatarFloorAdjuster";
        private const string TAAO = "Anatawa12.AvatarOptimizer.TraceAndOptimize";
        private const string TFury = "VF.Model.VRCFury";
        private const string TFixWD = "VF.Model.Feature.FixWriteDefaults";

        // ---------------- アバタールート検出 ----------------
        public static GameObject FindAvatarRoot(GameObject go)
        {
            if (go == null) return null;
            var descType = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descType != null)
            {
                var d = go.GetComponentInParent(descType, true);
                if (d != null) return d.gameObject;
            }
            Transform t = go.transform;
            GameObject found = null;
            while (t != null)
            {
                var a = t.GetComponent<Animator>();
                if (a != null && a.isHuman) found = t.gameObject;
                t = t.parent;
            }
            if (found != null) return found;
            var child = go.GetComponentInChildren<Animator>(true);
            return child != null && child.isHuman ? child.gameObject : null;
        }

        public static Transform GetHips(GameObject root)
        {
            var animator = root.GetComponent<Animator>() ?? root.GetComponentInChildren<Animator>(true);
            return animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        }

        // ---------------- パッケージ導入状況 ----------------
        public static bool IsInstalled(int step)
        {
            switch (step)
            {
                case StepAnchor: return true;
                case StepMeshSettings: return FindType(TMeshSettings) != null;
                case StepAAO: return FindType(TAAO) != null;
                case StepVRCFury: return FindType(TFury) != null && FindType(TFixWD) != null;
                case StepLAC: return FindLacType() != null;
                case StepFloor: return FindType(TFloorAdjuster) != null;
            }
            return false;
        }

        // ---------------- アバターに追加済みか ----------------
        public static bool IsPresent(GameObject root, int step)
        {
            if (root == null) return false;
            switch (step)
            {
                case StepAnchor: return FindAnchor(root) != null;
                case StepMeshSettings: { var t = FindType(TMeshSettings); return t != null && root.GetComponent(t) != null; }
                case StepAAO: { var t = FindType(TAAO); return t != null && root.GetComponent(t) != null; }
                case StepVRCFury:
                    {
                        var t = FindType(TFury);
                        if (t == null) return false;
                        return root.GetComponents(t).Any(c => HasFixWD(c));
                    }
                case StepLAC: { var t = FindLacType(); return t != null && root.GetComponent(t) != null; }
                case StepFloor: return FindFloor(root) != null;
            }
            return false;
        }

        // ==================================================================
        // セットアップ
        // ==================================================================
        public static void Apply(GameObject root, bool[] enabled, Transform anchorBone, bool syncChildren, List<string> log)
        {
            // アンカーの基準ボーン: 指定があればそれ(アバター配下に限る)、無ければ Hips
            var bone = anchorBone != null && anchorBone.IsChildOf(root.transform) ? anchorBone : GetHips(root);
            if (bone == null)
            {
                log.Add("✘ アンカーの基準ボーンが見つかりません (Humanoid 設定を確認)");
                return;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("MeroTools BasicAvatar Setup");
            int group = Undo.GetCurrentGroup();

            try
            {
                // MeshSettings はアンカーが必須なので、チェックが無くても無ければ作る
                Transform anchor = null;
                if (enabled[StepAnchor] || enabled[StepMeshSettings])
                {
                    anchor = CreateAnchor(root, bone);
                    log.Add($"✔ {AnchorName} 作成/更新 (基準ボーン: {bone.name})");
                }

                if (enabled[StepMeshSettings]) StepApplyMeshSettings(root, anchor, syncChildren, log);
                if (enabled[StepAAO]) StepApplyAAO(root, log);
                if (enabled[StepVRCFury]) StepApplyVRCFury(root, log);
                if (enabled[StepLAC]) StepApplyLAC(root, log);
                if (enabled[StepFloor]) StepApplyFloor(root, log);
            }
            catch (Exception e)
            {
                Debug.LogError("[MeroTools] セットアップ中にエラー: " + e);
                log.Add("✘ 例外: " + e.Message);
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }

            MarkDirty(root);
        }

        // ==================================================================
        // 削除
        // ==================================================================
        public static void Remove(GameObject root, bool[] enabled, List<string> log)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("MeroTools BasicAvatar Remove");
            int group = Undo.GetCurrentGroup();

            try
            {
                if (enabled[StepFloor])
                {
                    var t = FindFloor(root);
                    if (t != null)
                    {
                        var ft = FindType(TFloorAdjuster);
                        var c = ft != null ? t.GetComponent(ft) : null;
                        if (t.childCount == 0) Undo.DestroyObjectImmediate(t.gameObject);
                        else if (c != null) Undo.DestroyObjectImmediate(c);
                        log.Add("✔ FloorAdjuster 削除");
                    }
                }
                if (enabled[StepLAC])
                {
                    var lt = FindLacType();
                    var c = lt != null ? root.GetComponent(lt) : null;
                    if (c != null) { Undo.DestroyObjectImmediate(c); log.Add("✔ LAC TextureCompressor 削除"); }
                }
                if (enabled[StepVRCFury])
                {
                    int n = RemoveFixWD(root);
                    if (n > 0) log.Add($"✔ VRCFury Fix Write Defaults 削除 ({n})");
                }
                if (enabled[StepAAO])
                {
                    var at = FindType(TAAO);
                    var c = at != null ? root.GetComponent(at) : null;
                    if (c != null) { Undo.DestroyObjectImmediate(c); log.Add("✔ AAO Trace And Optimize 削除"); }
                }
                if (enabled[StepMeshSettings])
                {
                    var mt = FindType(TMeshSettings);
                    var c = mt != null ? root.GetComponent(mt) : null;
                    if (c != null)
                    {
                        Undo.DestroyObjectImmediate(c);
                        log.Add("✔ ルートの MA MeshSettings 削除 (配下の衣装の MeshSettings は元の値に戻せないため残しています)");
                    }
                }
                if (enabled[StepAnchor])
                {
                    var a = FindAnchor(root);
                    if (a != null) { Undo.DestroyObjectImmediate(a.gameObject); log.Add("✔ Anchor Object 削除"); }
                }
                if (log.Count == 0) log.Add("削除対象は見つかりませんでした");
            }
            catch (Exception e)
            {
                Debug.LogError("[MeroTools] 削除中にエラー: " + e);
                log.Add("✘ 例外: " + e.Message);
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }

            MarkDirty(root);
        }

        private static void MarkDirty(GameObject root)
        {
            EditorUtility.SetDirty(root);
            if (root.scene.IsValid() && !Application.isPlaying) EditorSceneManager.MarkSceneDirty(root.scene);
        }

        // ------------------------------------------------------------------
        // Hips アンカー
        // ------------------------------------------------------------------
        private static Transform CreateAnchor(GameObject root, Transform bone)
        {
            Transform anchor = FindAnchor(root);
            if (anchor != null && anchor.name != AnchorName)
            {
                Undo.RecordObject(anchor.gameObject, "Rename Anchor");
                anchor.name = AnchorName;
            }
            if (anchor == null)
            {
                var go = new GameObject(AnchorName);
                Undo.RegisterCreatedObjectUndo(go, "Create Hips Anchor");
                anchor = go.transform;
                anchor.SetParent(root.transform, false);
            }
            else Undo.RecordObject(anchor, "Update Hips Anchor");

            anchor.SetPositionAndRotation(bone.position, bone.rotation);
            anchor.localScale = Vector3.one;
            EditorUtility.SetDirty(anchor);
            return anchor;
        }

        // ------------------------------------------------------------------
        // MA MeshSettings
        // ------------------------------------------------------------------
        private static void StepApplyMeshSettings(GameObject root, Transform anchor, bool syncChildren, List<string> log)
        {
            var type = FindType(TMeshSettings);
            if (type == null) { log.Add("⚠ Modular Avatar 未導入: MeshSettings をスキップ"); return; }

            var rootComp = root.GetComponent(type);
            if (rootComp == null) rootComp = Undo.AddComponent(root, type);

            ApplyMeshSettings(rootComp, root, anchor);
            log.Add($"✔ ルートの MA MeshSettings 設定 → {AnchorName}");

            if (syncChildren)
            {
                int n = SyncChildrenToRoot(root);
                log.Add(n > 0 ? $"✔ 配下の MA MeshSettings {n} 個をルートに同期"
                              : "－ 配下に他の MA MeshSettings はありません");
            }
        }

        // 配下の MeshSettings をルートの設定に合わせる。
        // 戻り値: 同期した個数 / -1: MA 未導入 / -2: ルートに MeshSettings が無い
        public static int SyncChildrenToRoot(GameObject root)
        {
            var type = FindType(TMeshSettings);
            if (type == null) return -1;
            var rootComp = root.GetComponent(type);
            if (rootComp == null) return -2;

            var children = root.GetComponentsInChildren(type, true).Where(c => c != rootComp).Distinct().ToList();
            if (children.Count == 0) return 0;

            var src = new SerializedObject(rootComp);
            foreach (var child in children) CopyMeshSettings(src, child, root);
            MarkDirty(root);
            return children.Count;
        }

        private static void CopyMeshSettings(SerializedObject src, Component dst, GameObject avatarRoot)
        {
            Undo.RecordObject(dst, "MeroTools Sync MeshSettings");
            src.Update();

            // (a) 参照先がアバター内で解決できるなら AvatarObjectReference.Set でもメモリ上に反映
            foreach (var name in new[] { "ProbeAnchor", "RootBone" })
            {
                var pathProp = src.FindProperty(name + ".referencePath");
                if (pathProp == null || string.IsNullOrEmpty(pathProp.stringValue)) continue;
                var tr = avatarRoot.transform.Find(pathProp.stringValue);
                if (tr != null) CallReferenceSet(dst, name, tr.gameObject);
            }

            // (b) SerializedProperty でルートの値をそのままコピー
            var so = new SerializedObject(dst);
            so.Update();

            foreach (var name in new[] { "InheritProbeAnchor", "InheritBounds" })
            {
                var s = src.FindProperty(name);
                var d = so.FindProperty(name);
                if (s != null && d != null && s.propertyType == SerializedPropertyType.Enum)
                    d.enumValueIndex = s.enumValueIndex;
            }
            foreach (var name in new[] { "ProbeAnchor", "RootBone" })
            {
                var s = src.FindProperty(name);
                var d = so.FindProperty(name);
                if (s == null || d == null) continue;
                var sp = s.FindPropertyRelative("referencePath");
                var dp = d.FindPropertyRelative("referencePath");
                if (sp != null && dp != null) dp.stringValue = sp.stringValue;
                var so1 = s.FindPropertyRelative("targetObject");
                var do1 = d.FindPropertyRelative("targetObject");
                if (so1 != null && do1 != null) do1.objectReferenceValue = so1.objectReferenceValue;
            }
            var sb = src.FindProperty("Bounds");
            var db = so.FindProperty("Bounds");
            if (sb != null && db != null && sb.propertyType == SerializedPropertyType.Bounds)
                db.boundsValue = sb.boundsValue;

            so.ApplyModifiedProperties();
            if (PrefabUtility.IsPartOfPrefabInstance(dst)) PrefabUtility.RecordPrefabInstancePropertyModifications(dst);
            EditorUtility.SetDirty(dst);
        }

        private static void ApplyMeshSettings(Component comp, GameObject avatarRoot, Transform anchor)
        {
            Undo.RecordObject(comp, "MeroTools MeshSettings");
            string path = AnimationUtility.CalculateTransformPath(anchor, avatarRoot.transform);

            CallReferenceSet(comp, "ProbeAnchor", anchor.gameObject);
            CallReferenceSet(comp, "RootBone", anchor.gameObject);

            var bm = GetMember(comp.GetType(), "Bounds");
            if (bm != null && GetValue(bm, comp) is Bounds b && b.size == Vector3.zero)
                SetValue(bm, comp, new Bounds(Vector3.zero, Vector3.one * 2f));

            var so = new SerializedObject(comp);
            so.Update();
            SetEnumSet(so.FindProperty("InheritProbeAnchor"));
            SetEnumSet(so.FindProperty("InheritBounds"));
            SetRefProperty(so.FindProperty("ProbeAnchor"), path, anchor.gameObject);
            SetRefProperty(so.FindProperty("RootBone"), path, anchor.gameObject);

            var bp = so.FindProperty("Bounds");
            if (bp != null && bp.propertyType == SerializedPropertyType.Bounds && bp.boundsValue.size == Vector3.zero)
                bp.boundsValue = new Bounds(Vector3.zero, Vector3.one * 2f);
            so.ApplyModifiedProperties();

            if (PrefabUtility.IsPartOfPrefabInstance(comp))
                PrefabUtility.RecordPrefabInstancePropertyModifications(comp);
            EditorUtility.SetDirty(comp);
        }

        private static void CallReferenceSet(Component comp, string memberName, GameObject target)
        {
            var member = GetMember(comp.GetType(), memberName);
            if (member == null) return;
            object refObj = GetValue(member, comp);
            if (refObj == null)
            {
                try { refObj = Activator.CreateInstance(MemberType(member)); } catch { return; }
            }
            var set = refObj.GetType().GetMethods(AllInst).FirstOrDefault(m =>
                m.Name == "Set" && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(GameObject)));
            if (set != null)
            {
                try { set.Invoke(refObj, new object[] { target }); }
                catch (Exception e) { Debug.LogWarning("[MeroTools] AvatarObjectReference.Set 失敗: " + e.Message); }
            }
            SetValue(member, comp, refObj);
        }

        private static void SetEnumSet(SerializedProperty p)
        {
            if (p == null || p.propertyType != SerializedPropertyType.Enum) return;
            int idx = Array.FindIndex(p.enumNames, n => string.Equals(n, "Set", StringComparison.OrdinalIgnoreCase));
            p.enumValueIndex = idx >= 0 ? idx : 1;
        }

        private static void SetRefProperty(SerializedProperty p, string path, GameObject target)
        {
            if (p == null) return;
            var pp = p.FindPropertyRelative("referencePath");
            if (pp != null && pp.propertyType == SerializedPropertyType.String) pp.stringValue = path;
            var op = p.FindPropertyRelative("targetObject");
            if (op != null && op.propertyType == SerializedPropertyType.ObjectReference) op.objectReferenceValue = target;
        }

        // ------------------------------------------------------------------
        // AAO
        // ------------------------------------------------------------------
        private static void StepApplyAAO(GameObject root, List<string> log)
        {
            var type = FindType(TAAO);
            if (type == null) { log.Add("⚠ AAO 未導入: Trace And Optimize をスキップ"); return; }
            if (root.GetComponent(type) == null) Undo.AddComponent(root, type);
            log.Add("✔ AAO Trace And Optimize 追加");
        }

        // ------------------------------------------------------------------
        // VRCFury Fix Write Defaults
        // ------------------------------------------------------------------
        private static void StepApplyVRCFury(GameObject root, List<string> log)
        {
            var furyType = FindType(TFury);
            var fwdType = FindType(TFixWD);
            if (furyType == null || fwdType == null) { log.Add("⚠ VRCFury 未導入: Fix Write Defaults をスキップ"); return; }
            if (furyType.IsAbstract) { log.Add("✘ VF.Model.VRCFury が抽象クラスです"); return; }

            var comps = root.GetComponents(furyType);
            if (comps.Any(c => HasFixWD(c))) { log.Add("✔ VRCFury Fix Write Defaults は既に存在"); return; }

            // 壊れた(content が null)/空の既存コンポーネントがあれば再利用
            Component target = comps.FirstOrDefault(c => IsFuryEmpty(c));
            if (target == null) target = Undo.AddComponent(root, furyType);
            Undo.RecordObject(target, "MeroTools VRCFury FixWD");

            bool assigned = AssignFeatureSerialized(target, fwdType) || AssignFeatureReflection(target, furyType, fwdType);

            if (PrefabUtility.IsPartOfPrefabInstance(target))
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            EditorUtility.SetDirty(target);

            // 検証: 再読込して本当に入っているか確認
            if (assigned && HasFixWD(target))
            {
                log.Add("✔ VRCFury Fix Write Defaults (Auto) 追加");
            }
            else
            {
                log.Add("✘ VRCFury Fix Write Defaults の書き込みに失敗。VRCFury のフィールド構成: " + DumpProps(target));
            }
        }

        private static bool AssignFeatureSerialized(Component target, Type fwdType)
        {
            try
            {
                object feature = Activator.CreateInstance(fwdType);
                SetEnumByName(feature, "mode", "Auto");

                var so = new SerializedObject(target);
                so.Update();
                var content = so.FindProperty("content") ?? so.FindProperty("config.features");
                if (content == null) return false;

                if (content.isArray && content.propertyType != SerializedPropertyType.String)
                {
                    int i = content.arraySize;
                    content.InsertArrayElementAtIndex(i);
                    var el = content.GetArrayElementAtIndex(i);
                    el.managedReferenceValue = feature;
                }
                else if (content.propertyType == SerializedPropertyType.ManagedReference)
                {
                    content.managedReferenceValue = feature;
                }
                else return false;

                return so.ApplyModifiedProperties() || true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MeroTools] VRCFury(SerializedProperty) 失敗: " + e.Message);
                return false;
            }
        }

        private static bool AssignFeatureReflection(Component target, Type furyType, Type fwdType)
        {
            try
            {
                object feature = Activator.CreateInstance(fwdType);
                SetEnumByName(feature, "mode", "Auto");
                var m = GetMember(furyType, "content");
                if (m == null) return false;
                var mt = MemberType(m);
                if (typeof(IList).IsAssignableFrom(mt))
                {
                    var cur = GetValue(m, target);
                    if (cur == null)
                    {
                        cur = Activator.CreateInstance(mt.IsInterface
                            ? typeof(List<>).MakeGenericType(mt.GetGenericArguments()[0]) : mt);
                        SetValue(m, target, cur);
                    }
                    ((IList)cur).Add(feature);
                }
                else SetValue(m, target, feature);

                var so = new SerializedObject(target);
                so.Update();
                so.ApplyModifiedProperties();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MeroTools] VRCFury(Reflection) 失敗: " + e.Message);
                return false;
            }
        }

        private static IEnumerable<SerializedProperty> FeatureProps(SerializedObject so)
        {
            var content = so.FindProperty("content") ?? so.FindProperty("config.features");
            if (content == null) yield break;
            if (content.isArray && content.propertyType != SerializedPropertyType.String)
            {
                for (int i = 0; i < content.arraySize; i++) yield return content.GetArrayElementAtIndex(i);
            }
            else yield return content;
        }

        private static bool HasFixWD(Component fury)
        {
            if (fury == null) return false;
            var so = new SerializedObject(fury);
            foreach (var p in FeatureProps(so))
            {
                if (p.propertyType == SerializedPropertyType.ManagedReference &&
                    (p.managedReferenceFullTypename ?? "").Contains(TFixWD)) return true;
            }
            return false;
        }

        private static bool IsFuryEmpty(Component fury)
        {
            var so = new SerializedObject(fury);
            var content = so.FindProperty("content") ?? so.FindProperty("config.features");
            if (content == null) return true;
            if (content.isArray && content.propertyType != SerializedPropertyType.String) return content.arraySize == 0;
            if (content.propertyType == SerializedPropertyType.ManagedReference)
                return string.IsNullOrEmpty(content.managedReferenceFullTypename);
            return false;
        }

        private static int RemoveFixWD(GameObject root)
        {
            var furyType = FindType(TFury);
            if (furyType == null) return 0;
            int removed = 0;

            foreach (var comp in root.GetComponents(furyType).ToList())
            {
                var so = new SerializedObject(comp);
                so.Update();
                var content = so.FindProperty("content") ?? so.FindProperty("config.features");
                if (content == null) continue;

                if (content.isArray && content.propertyType != SerializedPropertyType.String)
                {
                    for (int i = content.arraySize - 1; i >= 0; i--)
                    {
                        var el = content.GetArrayElementAtIndex(i);
                        if (el.propertyType == SerializedPropertyType.ManagedReference &&
                            (el.managedReferenceFullTypename ?? "").Contains(TFixWD))
                        {
                            int before = content.arraySize;
                            content.DeleteArrayElementAtIndex(i);
                            if (content.arraySize == before) content.DeleteArrayElementAtIndex(i);
                            removed++;
                        }
                    }
                    so.ApplyModifiedProperties();
                }
                else if (content.propertyType == SerializedPropertyType.ManagedReference &&
                         (content.managedReferenceFullTypename ?? "").Contains(TFixWD))
                {
                    content.managedReferenceValue = null;
                    so.ApplyModifiedProperties();
                    removed++;
                }

                // 空になった VRCFury コンポーネントは削除 (壊れたまま残さない)
                if (IsFuryEmpty(comp)) Undo.DestroyObjectImmediate(comp);
                else EditorUtility.SetDirty(comp);
            }
            return removed;
        }

        private static string DumpProps(Component c)
        {
            var names = new List<string>();
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            if (it.NextVisible(true))
                do { names.Add(it.propertyPath + ":" + it.propertyType); } while (it.NextVisible(false));
            return string.Join(", ", names);
        }

        // ------------------------------------------------------------------
        // LAC
        // ------------------------------------------------------------------
        private static Type _lacType;
        private static bool _lacResolved;

        private static Type FindLacType()
        {
            if (!_lacResolved) { _lacType = ResolveLacType(); _lacResolved = true; }
            return _lacType;
        }

        private static Type ResolveLacType()
        {
            var t = FindType("dev.limitex.avatar.compressor.TextureCompressor");
            if (t != null) return t;
            return AllTypes().FirstOrDefault(x =>
                x.Name == "TextureCompressor" && typeof(Component).IsAssignableFrom(x) &&
                ((x.Namespace ?? "").ToLowerInvariant().Contains("limitex") ||
                 (x.Assembly.GetName().Name ?? "").ToLowerInvariant().Contains("compressor")));
        }

        private static void StepApplyLAC(GameObject root, List<string> log)
        {
            var type = FindLacType();
            if (type == null) { log.Add("⚠ LAC 未導入: TextureCompressor をスキップ"); return; }

            var comp = root.GetComponent(type);
            if (comp == null) comp = Undo.AddComponent(root, type);
            Undo.RecordObject(comp, "MeroTools LAC Preset");

            bool ok = false;
            var so = new SerializedObject(comp);
            so.Update();
            var p = so.FindProperty("preset") ?? so.FindProperty("Preset") ?? so.FindProperty("m_preset");
            if (p != null && p.propertyType == SerializedPropertyType.Enum)
            {
                int idx = Array.FindIndex(p.enumNames, n => Norm(n).Contains("high") && Norm(n).Contains("quality"));
                if (idx < 0) idx = Array.FindIndex(p.enumNames, n => Norm(n).Contains("high"));
                if (idx >= 0) { p.enumValueIndex = idx; so.ApplyModifiedProperties(); ok = true; }
            }
            if (!ok) ok = SetEnumByName(comp, "preset", "HighQuality");

            var apply = type.GetMethods(AllInst).FirstOrDefault(m =>
                m.GetParameters().Length == 0 && m.Name.IndexOf("ApplyPreset", StringComparison.OrdinalIgnoreCase) >= 0);
            if (apply != null) { try { apply.Invoke(comp, null); } catch { } }

            if (PrefabUtility.IsPartOfPrefabInstance(comp)) PrefabUtility.RecordPrefabInstancePropertyModifications(comp);
            EditorUtility.SetDirty(comp);
            log.Add(ok ? "✔ LAC TextureCompressor (High Quality) 追加"
                       : "⚠ LAC TextureCompressor は追加しましたがプリセットを設定できませんでした");
        }

        // ------------------------------------------------------------------
        // Floor Adjuster
        // ------------------------------------------------------------------
        private static void StepApplyFloor(GameObject root, List<string> log)
        {
            var type = FindType(TFloorAdjuster);
            if (type == null) { log.Add("⚠ MA Floor Adjuster が見つかりません (MA のバージョンを確認)"); return; }
            Transform t = FindFloor(root);
            if (t != null && t.name != FloorAdjusterName)
            {
                Undo.RecordObject(t.gameObject, "Rename FloorAdjuster");
                t.name = FloorAdjusterName;
            }
            if (t == null)
            {
                var go = new GameObject(FloorAdjusterName);
                Undo.RegisterCreatedObjectUndo(go, "Create FloorAdjuster");
                go.transform.SetParent(root.transform, false);
                t = go.transform;
            }
            if (t.GetComponent(type) == null) Undo.AddComponent(t.gameObject, type);
            EditorUtility.SetDirty(t.gameObject);
            log.Add("✔ MA Floor Adjuster 追加");
        }

        // ------------------------------------------------------------------
        // リフレクション
        // ------------------------------------------------------------------
        private static IEnumerable<Type> AllTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                foreach (var t in types) yield return t;
            }
        }

        // 全アセンブリ走査は重いので結果(見つからなかった場合も)をキャッシュ。ドメインリロードで自動クリア。
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        public static Type FindType(string fullName)
        {
            if (TypeCache.TryGetValue(fullName, out var cached)) return cached;
            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); } catch { }
                if (t != null) { found = t; break; }
            }
            TypeCache[fullName] = found;
            return found;
        }

        private static MemberInfo GetMember(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, AllInst | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                var p = t.GetProperty(name, AllInst | BindingFlags.DeclaredOnly);
                if (p != null && p.CanRead) return p;
            }
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetFields(AllInst | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (f != null) return f;
            }
            return null;
        }

        private static Type MemberType(MemberInfo m) => m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;
        private static object GetValue(MemberInfo m, object o) => m is FieldInfo f ? f.GetValue(o) : ((PropertyInfo)m).GetValue(o);
        private static void SetValue(MemberInfo m, object o, object v)
        {
            if (m is FieldInfo f) f.SetValue(o, v);
            else if (m is PropertyInfo p && p.CanWrite) p.SetValue(o, v);
        }
        private static string Norm(string s) => (s ?? "").Replace(" ", "").Replace("_", "").ToLowerInvariant();

        private static bool SetEnumByName(object obj, string memberName, string enumName)
        {
            var m = GetMember(obj.GetType(), memberName);
            if (m == null) return false;
            var et = MemberType(m);
            if (!et.IsEnum) return false;
            foreach (var n in Enum.GetNames(et))
                if (Norm(n) == Norm(enumName)) { SetValue(m, obj, Enum.Parse(et, n)); return true; }
            return false;
        }
    }

    // ======================================================================
    // メイン UI
    // ======================================================================
    public class MeroBasicAvatarWindow : EditorWindow
    {
        private static readonly string[] StepLabels =
        {
            "Anchor Object 作成",
            "MA MeshSettings (ルート + 配下すべて同期)",
            "AAO Trace And Optimize",
            "VRCFury Fix Write Defaults (Auto)",
            "LAC TextureCompressor (High Quality)",
            "MA Floor Adjuster",
        };

        private static readonly string[] StepDescs =
        {
            "指定ボーン (基本 Hips) と同じ位置・回転の空オブジェクト",
            "Anchor Override / Bounds を Set → Anchor Object",
            "ルートに TraceAndOptimize",
            "具象 VF.Model.VRCFury + FixWriteDefaults",
            "ルートに TextureCompressor + 高品質プリセット",
            "FloorAdjuster オブジェクト + コンポーネント",
        };

        private GameObject _avatar;
        private Transform _anchorBone;
        private GameObject _lastRoot;
        private readonly bool[] _installed = new bool[MeroCore.StepCount];
        private readonly bool[] _present = new bool[MeroCore.StepCount];
        private bool _dirty = true;
        private bool _syncChildren = true;
        private readonly bool[] _enabled = new bool[MeroCore.StepCount];
        private readonly List<string> _log = new List<string>();
        private Vector2 _scroll;
        private Vector2 _logScroll;

        public static void Open()
        {
            var w = GetWindow<MeroBasicAvatarWindow>(false, "MeroTools BasicAvatar", true);
            w.minSize = new Vector2(440, 520);
            w.TryTakeSelection();
            w.Show();
        }

        private void OnEnable()
        {
            for (int i = 0; i < _enabled.Length; i++)
                _enabled[i] = EditorPrefs.GetBool("MeroTools.Basic.step" + i, true);
            _syncChildren = EditorPrefs.GetBool("MeroTools.Basic.syncChildren", true);
            TryTakeSelection();
            Undo.undoRedoPerformed += MarkDirty;
            _dirty = true;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= MarkDirty;
            SaveToggles();
        }

        private void MarkDirty() { _dirty = true; Repaint(); }
        private void OnFocus() => _dirty = true;

        private void SaveToggles()
        {
            for (int i = 0; i < _enabled.Length; i++)
                EditorPrefs.SetBool("MeroTools.Basic.step" + i, _enabled[i]);
            EditorPrefs.SetBool("MeroTools.Basic.syncChildren", _syncChildren);
        }

        private void OnSelectionChange()
        {
            TryTakeSelection();
            _dirty = true;
            Repaint();
        }

        private void OnHierarchyChange() { _dirty = true; Repaint(); }

        private void TryTakeSelection()
        {
            var root = MeroCore.FindAvatarRoot(Selection.activeGameObject);
            if (root != null) _avatar = root;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("MeroTools BasicAvatar Setup", EditorStyles.boldLabel);

            // --- アバター ---
            EditorGUILayout.BeginVertical("box");
            _avatar = (GameObject)EditorGUILayout.ObjectField("アバター", _avatar, typeof(GameObject), true);
            var root = MeroCore.FindAvatarRoot(_avatar);
            if (root != _lastRoot)
            {
                _lastRoot = root;
                _anchorBone = root != null ? MeroCore.GetHips(root) : null; // アバターが変わったら Hips に戻す
                _dirty = true;
            }
            // 導入状況/追加済み判定は Layout 時に必要な時だけ更新 (毎フレーム走査しない)
            if (_dirty && Event.current.type == EventType.Layout)
            {
                for (int k = 0; k < MeroCore.StepCount; k++)
                {
                    _installed[k] = MeroCore.IsInstalled(k);
                    _present[k] = root != null && MeroCore.IsPresent(root, k);
                }
                _dirty = false;
            }
            if (_avatar == null)
                EditorGUILayout.HelpBox("Hierarchy でアバターを選択するか、上の欄にドラッグしてください。", MessageType.Info);
            else if (root == null)
                EditorGUILayout.HelpBox("アバタールートを特定できません (VRCAvatarDescriptor / Humanoid Animator が必要)。", MessageType.Warning);
            else
            {
                var hips = MeroCore.GetHips(root);
                EditorGUILayout.LabelField("ルート", root.name);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _anchorBone = (Transform)EditorGUILayout.ObjectField("アンカーボーン", _anchorBone, typeof(Transform), true);
                    using (new EditorGUI.DisabledScope(hips == null))
                        if (GUILayout.Button("Hips", GUILayout.Width(48))) _anchorBone = hips;
                }
                if (hips == null)
                    EditorGUILayout.HelpBox("Hips ボーンが見つかりません。アンカーボーンを手動で指定してください。", MessageType.Warning);
                if (_anchorBone != null && !_anchorBone.IsChildOf(root.transform))
                    EditorGUILayout.HelpBox("アンカーボーンはアバター配下のオブジェクトを指定してください (無効な場合 Hips を使います)。", MessageType.Warning);
            }
            EditorGUILayout.EndVertical();

            // --- 項目 ---
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("対象項目 (セットアップ / 削除 共通)", EditorStyles.boldLabel);
                if (GUILayout.Button("全選択", GUILayout.Width(60))) SetAll(true);
                if (GUILayout.Button("全解除", GUILayout.Width(60))) SetAll(false);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(230));
            for (int i = 0; i < MeroCore.StepCount; i++)
            {
                bool installed = _installed[i];
                bool present = _present[i];

                EditorGUILayout.BeginVertical("box");
                using (new EditorGUILayout.HorizontalScope())
                {
                    _enabled[i] = EditorGUILayout.ToggleLeft(StepLabels[i], _enabled[i], EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    var prev = GUI.color;
                    if (!installed) { GUI.color = new Color(1f, 0.5f, 0.5f); GUILayout.Label("未導入", GUILayout.Width(50)); }
                    else if (present) { GUI.color = new Color(0.5f, 1f, 0.6f); GUILayout.Label("追加済", GUILayout.Width(50)); }
                    else GUILayout.Label("", GUILayout.Width(50));
                    GUI.color = prev;
                }
                EditorGUILayout.LabelField(StepDescs[i], EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            _syncChildren = EditorGUILayout.ToggleLeft("セットアップ時、配下の MA MeshSettings もルートの設定に合わせる", _syncChildren);

            if (_enabled[MeroCore.StepMeshSettings] && !_enabled[MeroCore.StepAnchor])
                EditorGUILayout.HelpBox("MeshSettings には Anchor Object が必要なため、無ければ自動で作成します。", MessageType.None);

            // --- 実行ボタン ---
            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(root == null))
            {
                var prevBg0 = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.6f, 0.8f, 1f);
                if (GUILayout.Button("配下の MA MeshSettings をルートの設定に合わせる", GUILayout.Height(26)))
                {
                    _log.Clear();
                    int n = MeroCore.SyncChildrenToRoot(root);
                    if (n == -1) _log.Add("⚠ Modular Avatar 未導入です");
                    else if (n == -2) _log.Add("⚠ ルートに MA MeshSettings がありません。先にセットアップを実行してください");
                    else if (n == 0) _log.Add("配下に他の MA MeshSettings はありません");
                    else _log.Add($"✔ 配下の MA MeshSettings {n} 個をルートの設定に同期しました");
                    foreach (var l in _log) Debug.Log("[MeroTools] " + l);
                }
                GUI.backgroundColor = prevBg0;
            }
            EditorGUILayout.Space(2);

            bool canRun = root != null;
            bool anyChecked = _enabled.Any(b => b);
            using (new EditorGUI.DisabledScope(!canRun || !anyChecked))
            {
                var prevBg = GUI.backgroundColor;

                GUI.backgroundColor = new Color(0.55f, 1f, 0.65f);
                if (GUILayout.Button("✨ セットアップ実行", GUILayout.Height(34)))
                {
                    SaveToggles();
                    _log.Clear();
                    MeroCore.Apply(root, _enabled, _anchorBone, _syncChildren, _log);
                    foreach (var l in _log) Debug.Log("[MeroTools] " + l);
                    _dirty = true;
                    MeroKakuhenWindow.ShowWindow(new List<string>(_log));
                }

                GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                if (GUILayout.Button("🗑 チェックした項目をアバターから削除", GUILayout.Height(26)))
                {
                    if (EditorUtility.DisplayDialog("MeroTools",
                            $"「{root.name}」から、チェックした項目のコンポーネント/オブジェクトを削除します。\n(Ctrl+Z で元に戻せます)\n\n※ 配下の衣装の MeshSettings は元の値に戻せません。",
                            "削除する", "キャンセル"))
                    {
                        SaveToggles();
                        _log.Clear();
                        MeroCore.Remove(root, _enabled, _log);
                        _dirty = true;
                        foreach (var l in _log) Debug.Log("[MeroTools] " + l);
                    }
                }
                GUI.backgroundColor = prevBg;
            }

            // --- ログ ---
            if (_log.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("ログ", EditorStyles.boldLabel);
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, "box", GUILayout.MinHeight(70), GUILayout.MaxHeight(140));
                foreach (var l in _log) EditorGUILayout.LabelField(l, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndScrollView();
            }
        }

        private void SetAll(bool v)
        {
            for (int i = 0; i < _enabled.Length; i++) _enabled[i] = v;
        }
    }

    // ======================================================================
    // 完了ウィンドウ: パチンコ確変風 + 一文字ずつ虹色 (軽量版)
    // ・描画は Repaint イベント時のみ / 再描画は 30fps に制限
    // ・文字幅・星の位置・GUIContent・スタイルは初回に計算して使い回し
    // ======================================================================
    public class MeroKakuhenWindow : EditorWindow
    {
        private const string Message = "これで僕とおそろいだね！✨🌈🎰";
        private const string Headline = "すごい！最適化された！すごい！";
        private const float FrameInterval = 1f / 30f;
        private const int StarCount = 40;

        private static readonly GUIContent SparkBig = new GUIContent("✦");
        private static readonly GUIContent SparkSmall = new GUIContent("·");
        private static readonly GUIContent Seven = new GUIContent("7");
        private static readonly GUIContent HeadlineContent = new GUIContent(Headline);

        private List<string> _log = new List<string>();
        private GUIContent[] _chars;
        private float[] _charWidths;
        private float _totalWidth;
        private Vector2[] _stars;
        private float[] _starSpeed;
        private float _lastRepaint;
        private GUIStyle _charStyle, _logStyle, _sparkStyle, _titleStyle, _reelStyle;
        private Vector2 _scroll;

        public static void ShowWindow(List<string> log)
        {
            var w = GetWindow<MeroKakuhenWindow>(true, "MeroTools - すごい！最適化された！", true);
            w.minSize = new Vector2(560, 360);
            w.maxSize = new Vector2(560, 360);
            w._log = log ?? new List<string>();
            w.ShowUtility();
            w.Focus();
        }

        private void OnEnable() => EditorApplication.update += Tick;
        private void OnDisable() => EditorApplication.update -= Tick;

        private void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastRepaint < FrameInterval) return;
            _lastRepaint = now;
            Repaint();
        }

        private void InitCache()
        {
            if (_charStyle != null) return;

            _charStyle = new GUIStyle(EditorStyles.boldLabel)
            { fontSize = 30, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            _sparkStyle = new GUIStyle(EditorStyles.label)
            { fontSize = 18, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
            _reelStyle = new GUIStyle(_charStyle) { fontSize = 32 };
            _logStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

            // 一文字ずつ水平分割 (サロゲートペアを壊さない) + 幅を先に計算
            var list = new List<GUIContent>();
            var e = StringInfo.GetTextElementEnumerator(Message);
            while (e.MoveNext()) list.Add(new GUIContent((string)e.Current));
            _chars = list.ToArray();
            _charWidths = new float[_chars.Length];
            _totalWidth = 0f;
            for (int i = 0; i < _chars.Length; i++)
            {
                _charWidths[i] = Mathf.Max(_charStyle.CalcSize(_chars[i]).x, 16f);
                _totalWidth += _charWidths[i];
            }

            _stars = new Vector2[StarCount];
            _starSpeed = new float[StarCount];
            for (int i = 0; i < StarCount; i++)
            {
                float seed = i * 12.9898f;
                _stars[i] = new Vector2(Frac(Mathf.Sin(seed) * 43758.5453f), Frac(Mathf.Sin(seed * 1.7f) * 24634.6345f));
                _starSpeed[i] = 2f + Frac(seed) * 4f;
            }
        }

        private void OnGUI()
        {
            InitCache();

            // アニメーション描画は Repaint 時だけ (Layout 等の余計なイベントでは何もしない)
            if (Event.current.type == EventType.Repaint) DrawAnimation(Time.realtimeSinceStartup);

            var logRect = new Rect(20, 225, position.width - 40, 90);
            EditorGUI.DrawRect(logRect, new Color(0, 0, 0, 0.55f));
            GUILayout.BeginArea(new Rect(logRect.x + 6, logRect.y + 4, logRect.width - 12, logRect.height - 8));
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var old = GUI.contentColor;
            GUI.contentColor = Color.white;
            for (int i = 0; i < _log.Count; i++) GUILayout.Label(_log[i], _logStyle);
            GUI.contentColor = old;
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();

            if (GUI.Button(new Rect(position.width * 0.5f - 60, position.height - 38, 120, 28), "OK！"))
                Close();
        }

        private void DrawAnimation(float t)
        {
            float w = position.width, h = position.height;
            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 8f);

            // 背景
            EditorGUI.DrawRect(new Rect(0, 0, w, h), Color.HSVToRGB((t * 0.15f) % 1f, 0.9f, 0.18f + 0.12f * pulse));
            float stripe = w / 12f;
            float v = 0.5f + 0.5f * pulse;
            for (int i = 0; i < 12; i++)
            {
                var c = Color.HSVToRGB((t * 0.2f + i / 12f) % 1f, 0.8f, v);
                c.a = 0.18f;
                EditorGUI.DrawRect(new Rect(i * stripe, 0, stripe, h), c);
            }

            // キラキラ
            Color prev = GUI.color;
            for (int i = 0; i < StarCount; i++)
            {
                float tw = Mathf.Abs(Mathf.Sin(t * _starSpeed[i] + i * 12.9898f));
                var col = Color.HSVToRGB((t * 0.6f + i * 0.05f) % 1f, 0.35f, 1f);
                col.a = tw;
                GUI.color = col;
                GUI.Label(new Rect(_stars[i].x * w - 10, _stars[i].y * h - 10, 20, 20),
                    tw > 0.6f ? SparkBig : SparkSmall, _sparkStyle);
            }

            // 見出し
            GUI.color = new Color(1f, 0.95f, 0.2f, Mathf.Sin(t * 20f) > 0 ? 1f : 0.55f);
            GUI.Label(new Rect(0, 12, w, 34), HeadlineContent, _titleStyle);

            // メッセージ: 一文字ずつ HSV 虹色
            float scale = _totalWidth > w - 20f ? (w - 20f) / _totalWidth : 1f;
            float x = (w - _totalWidth * scale) * 0.5f;
            var shadow = new Color(0, 0, 0, 0.6f);
            for (int i = 0; i < _chars.Length; i++)
            {
                float hue = (t * 0.6f - i * 0.07f) % 1f;
                if (hue < 0f) hue += 1f;
                float shine = Mathf.Clamp01(0.75f + 0.25f * Mathf.Sin(t * 10f - i * 0.8f));

                float cw = _charWidths[i] * scale;
                var r = new Rect(x, 95f + Mathf.Sin(t * 6f + i * 0.6f) * 6f, cw, 48f);

                GUI.color = shadow;
                GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), _chars[i], _charStyle);
                GUI.color = Color.HSVToRGB(hue, 0.85f, shine);
                GUI.Label(r, _chars[i], _charStyle);
                x += cw;
            }

            // 7 7 7 リール
            const float cell = 44f, gap = 8f;
            float startX = (w - (cell * 3 + gap * 2)) * 0.5f;
            for (int i = 0; i < 3; i++)
            {
                var r = new Rect(startX + i * (cell + gap), 165f, cell, 42f);
                EditorGUI.DrawRect(r, new Color(1, 1, 1, 0.9f));
                GUI.color = Color.HSVToRGB((t * 1.2f + i * 0.2f) % 1f, 0.9f, 1f);
                GUI.Label(r, Seven, _reelStyle);
            }
            GUI.color = prev;
        }

        private static float Frac(float v) => v - Mathf.Floor(v);
    }
}
#endif
