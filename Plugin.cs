using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ESM26.DualManager
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class DualManagerPlugin : BasePlugin
    {
        public const string GUID = "esm26.dualmanager";
        public const string NAME = "ESM26 Dual Manager";
        public const string VERSION = "2.0.0";

        internal static ManualLogSource Logger;
        internal static ConfigEntry<KeyCode> PanelKey;
        internal static ConfigEntry<KeyCode> SwapKey;
        internal static ConfigEntry<KeyCode> DumpKey;

        public override void Load()
        {
            Logger = Log;

            PanelKey = Config.Bind("Hotkeys", "PanelKey", KeyCode.F10, "Открыть/закрыть панель");
            SwapKey = Config.Bind("Hotkeys", "SwapKey", KeyCode.F11, "Передать ход второму менеджеру");
            DumpKey = Config.Bind("Hotkeys", "DumpKey", KeyCode.F9, "Записать диагностику в файл");

            ClassInjector.RegisterTypeInIl2Cpp<DualManagerUI>();

            var host = new GameObject("ESM26_DualManager");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<DualManagerUI>();

            Logger.LogInfo($"{NAME} v{VERSION} загружен. {PanelKey.Value} — панель.");
        }
    }

    // ─────────────────────────── Доступ к игре ───────────────────────────

    internal static class GameBridge
    {
        private static Type _globalValues, _dataTeam, _dataTeams;
        private static bool _searched;

        private static Func<List<object>> _accessor;
        private static string _accessorDesc = "не найден";
        public static string AccessorDescription => _accessorDesc;

        /// <summary>Рискованный обход сцены включается пользователем вручную.</summary>
        public static bool AllowSceneScan = false;

        internal const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        internal const BindingFlags IF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static Type DataTeamType => _dataTeam;

        public static bool Ready => Resolve();

        private static bool Resolve()
        {
            if (_searched) return _globalValues != null;
            _searched = true;

            foreach (var t in AllGameTypes())
            {
                switch (t.Name)
                {
                    case "GlobalValues": if (_globalValues == null) _globalValues = t; break;
                    case "DataTeam": if (_dataTeam == null) _dataTeam = t; break;
                    case "DataTeams": if (_dataTeams == null) _dataTeams = t; break;
                }
                if (_globalValues != null && _dataTeam != null && _dataTeams != null) break;
            }
            return _globalValues != null;
        }

        internal static List<Type> AllGameTypes()
        {
            var list = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name;
                bool game = n == "Assembly-CSharp" || n == "EsportsManager" ||
                            (n.StartsWith("Il2Cpp", StringComparison.Ordinal) &&
                             n != "Il2Cppmscorlib" && n != "Il2CppSystem.Core" &&
                             n != "Il2CppInterop.Runtime");
                if (!game) continue;

                try { list.AddRange(asm.GetTypes()); }
                catch (ReflectionTypeLoadException e)
                {
                    foreach (var t in e.Types) if (t != null) list.Add(t);
                }
                catch { }
            }

            int Score(Type t)
            {
                var n = t.Name;
                if (n == "DataTeams") return 0;
                if (n.IndexOf("Team", StringComparison.OrdinalIgnoreCase) >= 0 &&
                n.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) < 0) return 1;
                if (n.EndsWith("Database", StringComparison.Ordinal)) return 2;
                if (n == "GlobalValues") return 3;
                if (n.EndsWith("Manager", StringComparison.Ordinal) ||
                    n.EndsWith("Engine", StringComparison.Ordinal) ||
                    n.EndsWith("Controller", StringComparison.Ordinal)) return 4;
                if (n.StartsWith("Data", StringComparison.Ordinal)) return 5;
                return 9;
            }
            list.Sort((x, y) => Score(x).CompareTo(Score(y)));
            return list;
        }

        internal static MemberInfo FindMember(Type t, params string[] names)
        {
            const BindingFlags F = SF | IF;
            foreach (var n in names)
            {
                try
                {
                    var p = t.GetProperty(n, F);
                    if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p;
                    var f = t.GetField(n, F);
                    if (f != null) return f;
                }
                catch { }
            }
            return null;
        }

        internal static object SafeGet(MemberInfo m, object inst)
        {
            try
            {
                if (m is PropertyInfo p) return p.GetValue(inst);
                if (m is FieldInfo f) return f.GetValue(inst);
            }
            catch { }
            return null;
        }

        private static void SafeSet(MemberInfo m, object inst, object val)
        {
            if (m is PropertyInfo p) p.SetValue(inst, val);
            else if (m is FieldInfo f) f.SetValue(inst, val);
        }

        /// <summary>
        /// ТОЛЬКО поля. Свойства в IL2CPP — это вызов нативного кода игры;
        /// на чужих объектах такой вызов может уронить процесс, и перехватить
        /// это исключением невозможно. Поэтому при обходе читаем лишь поля.
        /// </summary>
        internal static List<MemberInfo> Members(Type t, BindingFlags f)
        {
            var res = new List<MemberInfo>();
            try
            {
                foreach (var fi in t.GetFields(f))
                {
                    // Служебные указатели интеропа не несут данных.
                    var n = fi.Name;
                    if (n.StartsWith("NativeFieldInfoPtr", StringComparison.Ordinal) ||
                        n.StartsWith("NativeMethodInfoPtr", StringComparison.Ordinal) ||
                        n == "Pointer" || n == "ObjectClass") continue;
                    if (fi.FieldType == typeof(IntPtr)) continue;
                    res.Add(fi);
                }
            }
            catch { }
            return res;
        }

        // ── команда игрока ──

        public static object GetPlayerTeam()
        {
            if (!Ready) return null;
            var m = FindMember(_globalValues, "DataTeam", "dataTeam", "PlayerTeam", "playerTeam");
            return m == null ? null : SafeGet(m, null);
        }

        public static bool SetPlayerTeam(object team, out string error)
        {
            error = null;
            if (!Ready) { error = "игровые типы не найдены"; return false; }
            if (team == null) { error = "организация не найдена"; return false; }

            var m = FindMember(_globalValues, "DataTeam", "dataTeam", "PlayerTeam", "playerTeam");
            if (m == null) { error = "поле текущей организации не найдено"; return false; }

            try { SafeSet(m, null, team); }
            catch (Exception e) { error = e.Message; return false; }

            var now = GetPlayerTeam();
            if (now == null || !ReferenceEquals(now, team))
            {
                error = "игра не приняла смену организации";
                return false;
            }
            return true;
        }

        public static string TeamName(object team)
        {
            if (team == null) return "—";
            var m = FindMember(team.GetType(), "Name", "name", "TeamName", "ShortName");
            var v = SafeGet(m, team);
            var s = v?.ToString();
            return string.IsNullOrEmpty(s) ? team.GetType().Name : s;
        }

        // ── список организаций ──

        public static List<object> AllTeams()
        {
            var result = new List<object>();
            if (!Ready) return result;

            if (_accessor != null)
            {
                try
                {
                    var cached = _accessor();
                    if (cached != null && cached.Count > 0) return cached;
                }
                catch { }
                _accessor = null;
            }

            // Если прошлый поиск уронил игру, метка осталась на диске —
            // второй раз автоматически не пробуем.
            if (CrashMarkerExists())
            {
                DualManagerPlugin.Logger.LogWarning(
                    "Прошлый поиск завершился аварийно. Удалите файл esm26_scan.lock, чтобы попробовать снова.");
                _accessorDesc = "поиск отключён после сбоя";
                return result;
            }

            SetCrashMarker(true);
            try { Scan(result); }
            finally { SetCrashMarker(false); }

            return result;
        }

        private static bool Scan(List<object> result)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 1. Статические члены игровых типов
            foreach (var type in AllGameTypes())
            {
                if (sw.ElapsedMilliseconds > 5000) break;

                foreach (var m in Members(type, SF))
                {
                    var v = SafeGet(m, null);
                    if (v == null) continue;

                    var tmp = new List<object>();
                    if (Extract(v, tmp))
                    {
                        var cm = m;
                        _accessor = () => { var l = new List<object>(); var val = SafeGet(cm, null); if (val != null) Extract(val, l); return l; };
                        _accessorDesc = $"{type.Name}.{m.Name}";
                        result.AddRange(tmp);
                        return true;
                    }
                }

                var instM = FindMember(type, "Instance", "instance", "Current", "current");
                if (instM == null) continue;
                var inst = SafeGet(instM, null);
                if (inst == null) continue;

                foreach (var m in Members(inst.GetType(), IF))
                {
                    var v = SafeGet(m, inst);
                    if (v == null) continue;

                    var tmp = new List<object>();
                    if (Extract(v, tmp))
                    {
                        var ci = instM; var cm = m;
                        _accessor = () =>
                        {
                            var l = new List<object>();
                            var i2 = SafeGet(ci, null);
                            if (i2 == null) return l;
                            var val = SafeGet(cm, i2);
                            if (val != null) Extract(val, l);
                            return l;
                        };
                        _accessorDesc = $"{type.Name}.Instance.{m.Name}";
                        result.AddRange(tmp);
                        return true;
                    }
                }
            }

            // 2. Обход графа от известного объекта: команда игрока почти
            //    наверняка ссылается на турнир/лигу, где есть все остальные.
            if (ScanFromPlayerTeam(result)) return true;

            // 3. Компоненты сцены — только по явному запросу: этот обход
            //    создаёт обёртки по указателям и может уронить игру.
            if (AllowSceneScan) return ScanSceneComponents(result);
            return false;
        }

        /// <summary>
        /// Поиск в ширину от команды игрока и сегодняшних матчей.
        /// Запоминает всю цепочку полей, чтобы потом повторить путь.
        /// </summary>
        private sealed class Node
        {
            public string RootKey;
            public List<MemberInfo> Chain;
            public object Value;
            public int Depth;
        }

        private static bool ScanFromPlayerTeam(List<object> result)
        {
            var log = DualManagerPlugin.Logger;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var queue = new Queue<Node>();

            var pt = GetPlayerTeam();
            if (pt != null)
                queue.Enqueue(new Node { RootKey = "PlayerTeam", Chain = new List<MemberInfo>(), Value = pt, Depth = 0 });

            var tm = FindMember(_globalValues, "TodayMatches", "_TodayMatches_k__BackingField");
            var tmv = tm != null ? SafeGet(tm, null) : null;
            if (tmv != null)
                queue.Enqueue(new Node { RootKey = "TodayMatches", Chain = new List<MemberInfo>(), Value = tmv, Depth = 0 });

            if (queue.Count == 0) return false;

            var visited = new HashSet<object>(ReferenceComparer.Instance);
            int examined = 0;

            while (queue.Count > 0)
            {
                if (sw.ElapsedMilliseconds > 5000 || examined > 2500)
                {
                    log.LogWarning("Обход графа прерван по ограничению.");
                    break;
                }

                var cur = queue.Dequeue();
                var node = cur.Value;
                if (node == null || !visited.Add(node)) continue;
                examined++;
                if (cur.Depth > 3) continue;

                var t = node.GetType();
                if (t.IsPrimitive || t.IsEnum || node is string) continue;

                foreach (var m in Members(t, IF))
                {
                    var v = SafeGet(m, node);
                    if (v == null) continue;

                    var vt = v.GetType();
                    if (vt.IsPrimitive || vt.IsEnum || v is string) continue;

                    var chain = new List<MemberInfo>(cur.Chain) { m };

                    var probe = new List<object>();
                    if (Enumerate(v, probe))
                    {
                        var teams = LooksLikeTeams(probe) ? probe : UnwrapPairs(probe);
                        if (teams != null && LooksLikeTeams(teams) && teams.Count >= 10)
                        {
                            var rootKey = cur.RootKey;
                            var chainCopy = chain;
                            _accessor = () => WalkChain(rootKey, chainCopy);
                            _accessorDesc = rootKey + "." + string.Join(".", chain.ConvertAll(x => x.Name));
                            log.LogInfo($"Список организаций найден обходом графа: {_accessorDesc}, {teams.Count} шт.");
                            result.AddRange(teams);
                            return true;
                        }

                        int taken = 0;
                        foreach (var el in probe)
                        {
                            if (el == null || taken++ > 25) break;
                            var et = el.GetType();
                            if (et.IsPrimitive || et.IsEnum || el is string) continue;
                            queue.Enqueue(new Node { RootKey = cur.RootKey, Chain = chain, Value = el, Depth = cur.Depth + 1 });
                        }
                        continue;
                    }

                    queue.Enqueue(new Node { RootKey = cur.RootKey, Chain = chain, Value = v, Depth = cur.Depth + 1 });
                }
            }
            return false;
        }

        /// <summary>Повторяет сохранённый путь и возвращает список организаций.</summary>
        private static List<object> WalkChain(string rootKey, List<MemberInfo> chain)
        {
            var list = new List<object>();
            object cur = rootKey == "PlayerTeam"
                ? GetPlayerTeam()
                : SafeGet(FindMember(_globalValues, "TodayMatches", "_TodayMatches_k__BackingField"), null);

            for (int i = 0; i < chain.Count && cur != null; i++)
                cur = SafeGet(chain[i], cur);

            if (cur == null) return list;

            var probe = new List<object>();
            if (Enumerate(cur, probe))
            {
                var teams = LooksLikeTeams(probe) ? probe : UnwrapPairs(probe);
                if (teams != null) list.AddRange(teams);
            }
            return list;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>
        /// Обходит компоненты Unity в сцене и ищет в их полях коллекцию организаций.
        /// Нужно потому, что игровые контейнеры не имеют статических точек входа.
        /// </summary>
        internal static bool ScanSceneComponents(List<object> result)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var log = DualManagerPlugin.Logger;
            var seenTypes = new HashSet<string>();

            List<object> comps;
            try { comps = SceneComponents(); }
            catch (Exception e) { log.LogWarning("Обход сцены: " + e.Message); return false; }

            // Сначала самые вероятные компоненты.
            comps.Sort((a, b) => CompScore(a).CompareTo(CompScore(b)));

            foreach (var wrapper in comps)
            {
                if (sw.ElapsedMilliseconds > 12000) { log.LogWarning("Обход сцены прерван по времени."); break; }
                if (wrapper == null) continue;

                var wt = wrapper.GetType();
                if (!seenTypes.Add(wt.FullName)) continue;

                foreach (var m in Members(wt, IF))
                {
                    var v = SafeGet(m, wrapper);
                    if (v == null) continue;

                    var tmp = new List<object>();
                    if (Extract(v, tmp))
                    {
                        var capturedTypeName = wt.FullName;
                        var cm = m;
                        _accessor = () =>
                        {
                            var l = new List<object>();
                            var live = FindComponentOfType(capturedTypeName);
                            if (live == null) return l;
                            var val = SafeGet(cm, live);
                            if (val != null) Extract(val, l);
                            return l;
                        };
                        _accessorDesc = $"{wt.Name}.{m.Name} (сцена)";
                        log.LogInfo($"Список организаций найден: {_accessorDesc}, {tmp.Count} шт.");
                        result.AddRange(tmp);
                        return true;
                    }
                }
            }
            return false;
        }

        private static int CompScore(object o)
        {
            var n = o?.GetType().Name ?? "";
            if (n.IndexOf("Organization", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (n.IndexOf("Team", StringComparison.OrdinalIgnoreCase) >= 0 &&
                n.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) < 0) return 1;
            if (n == "GameManager") return 2;
            if (n.EndsWith("Database", StringComparison.Ordinal)) return 3;
            if (n.EndsWith("Manager", StringComparison.Ordinal) ||
                n.EndsWith("Controller", StringComparison.Ordinal)) return 4;
            return 9;
        }

        /// <summary>Все компоненты сцены, приведённые к своим настоящим типам.</summary>
        private static List<object> SceneComponents()
        {
            var res = new List<object>();
            var byName = TypesByFullName();

            var all = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>());
            if (all == null) return res;

            for (int i = 0; i < all.Length; i++)
            {
                var o = all[i];
                if (o == null) continue;
                var w = AsRealType(o, byName);
                if (w != null) res.Add(w);
            }
            return res;
        }

        private static Dictionary<string, Type> _typesByName;

        private static Dictionary<string, Type> TypesByFullName()
        {
            if (_typesByName != null) return _typesByName;
            _typesByName = new Dictionary<string, Type>();
            foreach (var t in AllGameTypes())
            {
                var fn = t.FullName;
                if (string.IsNullOrEmpty(fn)) continue;
                var key = fn.StartsWith("Il2Cpp", StringComparison.Ordinal) ? fn.Substring(6) : fn;
                if (!_typesByName.ContainsKey(key)) _typesByName[key] = t;
                if (!_typesByName.ContainsKey(fn)) _typesByName[fn] = t;
            }
            return _typesByName;
        }

        /// <summary>Приводит объект Unity к его настоящему управляемому типу.</summary>
        private static object AsRealType(UnityEngine.Object o, Dictionary<string, Type> byName)
        {
            try
            {
                var native = o.GetIl2CppType();
                var fn = native?.FullName;
                if (string.IsNullOrEmpty(fn)) return null;
                if (!byName.TryGetValue(fn, out var t)) return null;
                return Activator.CreateInstance(t, o.Pointer);
            }
            catch { return null; }
        }

        private static object FindComponentOfType(string fullName)
        {
            try
            {
                var byName = TypesByFullName();
                var all = Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>());
                if (all == null) return null;

                for (int i = 0; i < all.Length; i++)
                {
                    var o = all[i];
                    if (o == null) continue;
                    var w = AsRealType(o, byName);
                    if (w != null && w.GetType().FullName == fullName) return w;
                }
            }
            catch { }
            return null;
        }

        private static bool Extract(object src, List<object> into)
        {
            return ExtractDepth(src, into, 0);
        }

        private static bool ExtractDepth(object src, List<object> into, int depth)
        {
            if (src == null || depth > 2) return false;
            var t = src.GetType();
            if (t.IsPrimitive || t.IsEnum || src is string) return false;

            // Прямая коллекция организаций
            if (Enumerate(src, into) && LooksLikeTeams(into)) return true;

            // Словарь: организации могут быть значениями
            if (into.Count > 0)
            {
                var unwrapped = UnwrapPairs(into);
                if (unwrapped != null && LooksLikeTeams(unwrapped))
                {
                    into.Clear();
                    into.AddRange(unwrapped);
                    return true;
                }
            }
            into.Clear();

            if (depth >= 2) return false;

            foreach (var m in Members(t, IF))
            {
                var v = SafeGet(m, src);
                if (v == null) continue;
                if (ExtractDepth(v, into, depth + 1)) return true;
                into.Clear();
            }
            return false;
        }

        /// <summary>Достаёт значения из элементов вида KeyValuePair.</summary>
        private static List<object> UnwrapPairs(List<object> items)
        {
            if (items.Count == 0) return null;
            var first = items[0];
            if (first == null) return null;
            if (first.GetType().Name.IndexOf("KeyValuePair", StringComparison.Ordinal) < 0) return null;

            var res = new List<object>();
            foreach (var it in items)
            {
                if (it == null) continue;
                var m = FindMember(it.GetType(), "Value", "value");
                var v = SafeGet(m, it);
                if (v != null) res.Add(v);
            }
            return res.Count > 0 ? res : null;
        }

        /// <summary>
        /// Коллекции в IL2CPP не реализуют обычный IEnumerable,
        /// поэтому пробуем несколько способов обхода.
        /// </summary>
        internal static bool Enumerate(object src, List<object> into)
        {
            if (src == null || src is string) return false;
            var t = src.GetType();
            if (t.IsPrimitive || t.IsEnum || t == typeof(DateTime)) return false;

            try
            {
                if (src is System.Collections.IEnumerable en)
                {
                    foreach (var x in en) if (x != null) into.Add(x);
                    if (into.Count > 0) return true;
                }
            }
            catch { into.Clear(); }

            try
            {
                var countM = FindMember(t, "Count", "count", "Length", "length");
                var cv = countM != null ? SafeGet(countM, src) : null;
                if (cv != null)
                {
                    int n = 0;
                    try { n = Convert.ToInt32(cv); } catch { n = 0; }
                    if (n > 0 && n <= 20000)
                    {
                        PropertyInfo idx = null;
                        foreach (var p in t.GetProperties(IF))
                        {
                            var ps = p.GetIndexParameters();
                            if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) { idx = p; break; }
                        }
                        MethodInfo get = null;
                        if (idx == null)
                        {
                            foreach (var mi in t.GetMethods(IF))
                            {
                                if (mi.Name != "get_Item" && mi.Name != "Get") continue;
                                var ps = mi.GetParameters();
                                if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) { get = mi; break; }
                            }
                        }
                        if (idx != null || get != null)
                        {
                            for (int i = 0; i < n; i++)
                            {
                                object x = null;
                                try { x = idx != null ? idx.GetValue(src, new object[] { i }) : get.Invoke(src, new object[] { i }); }
                                catch { break; }
                                if (x != null) into.Add(x);
                            }
                            if (into.Count > 0) return true;
                        }
                    }
                }
            }
            catch { into.Clear(); }

            try
            {
                var ge = t.GetMethod("GetEnumerator", IF, null, Type.EmptyTypes, null);
                if (ge != null)
                {
                    var e = ge.Invoke(src, null);
                    if (e != null)
                    {
                        var et = e.GetType();
                        var mn = et.GetMethod("MoveNext", IF, null, Type.EmptyTypes, null);
                        var cur = FindMember(et, "Current", "current");
                        if (mn != null && cur != null)
                        {
                            int guard = 0;
                            while (guard++ < 20000)
                            {
                                object ok;
                                try { ok = mn.Invoke(e, null); } catch { break; }
                                if (!(ok is bool b) || !b) break;
                                var x = SafeGet(cur, e);
                                if (x != null) into.Add(x);
                            }
                            if (into.Count > 0) return true;
                        }
                    }
                }
            }
            catch { into.Clear(); }

            into.Clear();
            return false;
        }

        private static bool LooksLikeTeams(List<object> items)
        {
            if (items.Count < 3) return false;

            // Считаем списком организаций только коллекцию именно из DataTeam.
            // Иначе ловятся посторонние коллекции (достижения, строки, цвета).
            int checkedCount = 0, matched = 0;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (++checkedCount > 5) break;

                if (_dataTeam != null)
                {
                    if (_dataTeam.IsInstanceOfType(it)) matched++;
                }
                else if (it.GetType().Name == "DataTeam") matched++;
            }
            return checkedCount > 0 && matched == checkedCount;
        }

        // ── защита от повторного падения ──

        private static string MarkerPath() => Path.Combine(Paths.ConfigPath, "esm26_scan.lock");

        internal static bool CrashMarkerExists()
        {
            try { return File.Exists(MarkerPath()); }
            catch { return false; }
        }

        internal static void SetCrashMarker(bool on)
        {
            try
            {
                if (on) File.WriteAllText(MarkerPath(), DateTime.Now.ToString());
                else if (File.Exists(MarkerPath())) File.Delete(MarkerPath());
            }
            catch { }
        }

        internal static void ClearCrashMarker() => SetCrashMarker(false);

        // ── диагностика в файл ──

        public static string WriteDump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("ESM26 Dual Manager — диагностика");
            sb.AppendLine("=================================");
            sb.AppendLine($"Дата: {DateTime.Now}");
            sb.AppendLine();

            if (!Resolve())
            {
                sb.AppendLine("Игровые типы НЕ найдены. Загруженные сборки:");
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    sb.AppendLine("  " + asm.GetName().Name);
                return Save(sb);
            }

            sb.AppendLine($"GlobalValues : {_globalValues?.FullName}");
            sb.AppendLine($"DataTeam     : {_dataTeam?.FullName}");
            sb.AppendLine($"DataTeams    : {_dataTeams?.FullName}");
            var cur = GetPlayerTeam();
            sb.AppendLine($"Команда игрока: {(cur == null ? "null" : cur.GetType().FullName + " / " + TeamName(cur))}");
            sb.AppendLine($"Источник списка: {_accessorDesc}");
            sb.AppendLine();

            int printed = 0;
            foreach (var t in AllGameTypes())
            {
                var n = t.Name;
                // "Steam" содержит "team" — такие типы отсеиваем.
                bool teamish = n.IndexOf("Team", StringComparison.OrdinalIgnoreCase) >= 0 &&
                               n.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) < 0;
                bool interesting =
                    teamish ||
                    n.IndexOf("Organization", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Database", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n == "GlobalValues" || n == "GameManager" ||
                    n == "SlotData" || n == "SlotController";
                if (!interesting) continue;
                if (printed++ > 60) { sb.AppendLine("... (обрезано)"); break; }

                sb.AppendLine($"=== {t.FullName} ===");
                try
                {
                    foreach (var m in Members(t, SF))
                        sb.AppendLine("  static " + Describe(m, null));

                    var im = FindMember(t, "Instance", "instance", "Current", "current");
                    var inst = im != null ? SafeGet(im, null) : null;
                    if (inst != null)
                    {
                        sb.AppendLine($"  -- Instance ({inst.GetType().Name}) --");
                        foreach (var m in Members(inst.GetType(), IF))
                            sb.AppendLine("    inst " + Describe(m, inst));
                    }
                }
                catch (Exception e) { sb.AppendLine("  ошибка: " + e.Message); }
                sb.AppendLine();
            }

            // Все поля команды игрока — оттуда идёт обход графа.
            sb.AppendLine();
            sb.AppendLine("########## ПОЛЯ КОМАНДЫ ИГРОКА ##########");
            try
            {
                var pt = GetPlayerTeam();
                if (pt == null) sb.AppendLine("команда игрока = null");
                else
                {
                    sb.AppendLine($"тип: {pt.GetType().FullName}");
                    foreach (var m in Members(pt.GetType(), IF))
                    {
                        var line = Describe(m, pt);
                        if (line.EndsWith("= null", StringComparison.Ordinal)) continue;
                        sb.AppendLine("    " + line);
                    }
                }
            }
            catch (Exception e) { sb.AppendLine("ошибка: " + e.Message); }

            // Компоненты сцены: там живут GameManager, DataOrganizationList и т.п.
            sb.AppendLine();
            sb.AppendLine("########## КОМПОНЕНТЫ СЦЕНЫ ##########");
            try
            {
                var comps = SceneComponents();
                sb.AppendLine($"Всего компонентов: {comps.Count}");
                var seen = new HashSet<string>();
                int shown = 0;
                comps.Sort((a, b) => CompScore(a).CompareTo(CompScore(b)));
                foreach (var c in comps)
                {
                    var ct = c.GetType();
                    if (!seen.Add(ct.FullName)) continue;
                    if (CompScore(c) >= 9) continue;      // только вероятные
                    if (shown++ > 40) { sb.AppendLine("... (обрезано)"); break; }

                    sb.AppendLine($"=== [сцена] {ct.FullName} ===");
                    foreach (var m in Members(ct, IF))
                    {
                        var line = Describe(m, c);
                        if (line.EndsWith("= null", StringComparison.Ordinal)) continue;
                        sb.AppendLine("    " + line);
                    }
                }
            }
            catch (Exception e) { sb.AppendLine("Ошибка обхода сцены: " + e); }

            return Save(sb);
        }

        private static string Describe(MemberInfo m, object inst)
        {
            var mt = m is PropertyInfo p ? p.PropertyType.Name : ((FieldInfo)m).FieldType.Name;
            var val = SafeGet(m, inst);
            if (val == null) return $"{mt} {m.Name} = null";

            var probe = new List<object>();
            if (Enumerate(val, probe) && probe.Count > 0)
                return $"{mt} {m.Name}  -> КОЛЛЕКЦИЯ {probe.Count} шт., элемент {probe[0].GetType().Name}";

            return $"{mt} {m.Name} = есть значение";
        }

        private static string Save(StringBuilder sb)
        {
            var path = Path.Combine(Paths.ConfigPath, "esm26_dump.txt");
            try
            {
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                DualManagerPlugin.Logger.LogInfo("Диагностика записана: " + path);
                return path;
            }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogError("Не удалось записать диагностику: " + e.Message);
                return "ошибка записи: " + e.Message;
            }
        }
    }

    // ─────────────────────────── Настройки ───────────────────────────

    internal class Slots
    {
        public string OrgA = "", OrgB = "", Current = "A";

        private static string PathOf() => Path.Combine(Paths.ConfigPath, "esm26.dualmanager.slots.txt");

        public static Slots Load()
        {
            var s = new Slots();
            try
            {
                var p = PathOf();
                if (!File.Exists(p)) return s;
                foreach (var line in File.ReadAllLines(p))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim();
                    if (k == "OrgA") s.OrgA = v;
                    else if (k == "OrgB") s.OrgB = v;
                    else if (k == "Current") s.Current = v;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try { File.WriteAllLines(PathOf(), new[] { "OrgA=" + OrgA, "OrgB=" + OrgB, "Current=" + Current }); }
            catch { }
        }
    }

    // ─────────────────────────── Интерфейс ───────────────────────────

    public class DualManagerUI : MonoBehaviour
    {
        public DualManagerUI(IntPtr ptr) : base(ptr) { }

        private Slots _slots;
        private bool _open;
        private int _picking;          // 0 нет, 1 менеджер A, 2 менеджер B
        private int _cursor;
        private int _scrollTop;
        private string _filter = "";
        private string _status = "";
        private List<object> _teams = new List<object>();
        private bool _boxBroken;       // GUI.Box может быть вырезан

        private const int ROWS = 14;

        private void Start()
        {
            _slots = Slots.Load();
            // GUILayout вырезан из сборки игры: без этого ломается цикл событий IMGUI.
            try { useGUILayout = false; } catch { }
        }

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(DualManagerPlugin.PanelKey.Value))
                {
                    _open = !_open;
                }

                if (Input.GetKeyDown(DualManagerPlugin.SwapKey.Value)) SwapTurn();

                if (Input.GetKeyDown(DualManagerPlugin.DumpKey.Value))
                    _status = "Диагностика: " + GameBridge.WriteDump();

                if (_open) Keys();
            }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogError("Клавиши: " + e);
            }
        }

        private void Keys()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_picking != 0) { _picking = 0; _filter = ""; }
                else _open = false;
                return;
            }

            if (_picking != 0)
            {
                var list = Filtered();

                if (Input.GetKeyDown(KeyCode.DownArrow)) Move(1, list.Count);
                if (Input.GetKeyDown(KeyCode.UpArrow)) Move(-1, list.Count);
                if (Input.GetKeyDown(KeyCode.PageDown)) Move(ROWS, list.Count);
                if (Input.GetKeyDown(KeyCode.PageUp)) Move(-ROWS, list.Count);

                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (_cursor >= 0 && _cursor < list.Count) Assign(GameBridge.TeamName(list[_cursor]));
                    return;
                }

                var typed = Input.inputString;
                if (!string.IsNullOrEmpty(typed))
                {
                    foreach (var ch in typed)
                    {
                        if (ch == '\b') { if (_filter.Length > 0) _filter = _filter.Substring(0, _filter.Length - 1); }
                        else if (ch != '\n' && ch != '\r') _filter += ch;
                    }
                    _cursor = 0; _scrollTop = 0;
                }
                return;
            }

            if (Input.GetKeyDown(KeyCode.Alpha1)) { _picking = 1; _cursor = 0; _scrollTop = 0; _filter = ""; if (_teams.Count == 0) Refresh(); }
            if (Input.GetKeyDown(KeyCode.Alpha2)) { _picking = 2; _cursor = 0; _scrollTop = 0; _filter = ""; if (_teams.Count == 0) Refresh(); }
            if (Input.GetKeyDown(KeyCode.Q)) TakeCurrent(1);
            if (Input.GetKeyDown(KeyCode.W)) TakeCurrent(2);

            if (Input.GetKeyDown(KeyCode.R))
            {
                GameBridge.AllowSceneScan = false;
                Refresh();
            }

            if (Input.GetKeyDown(KeyCode.T))
            {
                GameBridge.AllowSceneScan = true;
                _status = "Расширенный поиск... (может занять время)";
                Refresh();
                GameBridge.AllowSceneScan = false;
            }

            if (Input.GetKeyDown(KeyCode.U))
            {
                GameBridge.ClearCrashMarker();
                _status = "Блокировка снята — можно искать снова (R).";
            }
        }

        private void Move(int delta, int count)
        {
            if (count == 0) { _cursor = 0; _scrollTop = 0; return; }
            _cursor = Mathf.Clamp(_cursor + delta, 0, count - 1);
            if (_cursor < _scrollTop) _scrollTop = _cursor;
            if (_cursor >= _scrollTop + ROWS) _scrollTop = _cursor - ROWS + 1;
        }

        private List<object> Filtered()
        {
            var list = new List<object>();
            foreach (var t in _teams)
            {
                var nm = GameBridge.TeamName(t);
                if (!string.IsNullOrEmpty(_filter) &&
                    nm.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(t);
            }
            return list;
        }

        private void Refresh()
        {
            _teams = GameBridge.AllTeams();
            if (_teams.Count > 0)
                _status = $"Организаций: {_teams.Count} (источник: {GameBridge.AccessorDescription})";
            else if (GameBridge.CrashMarkerExists())
                _status = "Поиск заблокирован после сбоя. Нажмите U, затем R.";
            else
                _status = "Не найдено. Попробуйте T (расширенный поиск), затем F9 для диагностики.";
        }

        private void Assign(string nm)
        {
            if (_picking == 1) { _slots.OrgA = nm; _slots.Current = "A"; }
            else if (_picking == 2) { _slots.OrgB = nm; }
            _slots.Save();
            _status = $"Менеджер {_picking} = {nm}";
            _picking = 0; _filter = ""; _cursor = 0; _scrollTop = 0;
        }

        private void TakeCurrent(int slot)
        {
            var nm = GameBridge.TeamName(GameBridge.GetPlayerTeam());
            if (slot == 1) { _slots.OrgA = nm; _slots.Current = "A"; }
            else { _slots.OrgB = nm; }
            _slots.Save();
            _status = $"Менеджер {slot} = {nm}";
        }

        private void SwapTurn()
        {
            if (string.IsNullOrEmpty(_slots.OrgA) || string.IsNullOrEmpty(_slots.OrgB))
            {
                _status = "Сначала назначьте обе организации (клавиши 1 и 2).";
                return;
            }

            var target = _slots.Current == "A" ? _slots.OrgB : _slots.OrgA;
            if (_teams.Count == 0) Refresh();

            object team = null;
            foreach (var t in _teams)
                if (string.Equals(GameBridge.TeamName(t), target, StringComparison.OrdinalIgnoreCase)) { team = t; break; }

            if (team == null) { _status = $"Организация «{target}» не найдена."; return; }

            if (GameBridge.SetPlayerTeam(team, out var err))
            {
                _slots.Current = _slots.Current == "A" ? "B" : "A";
                _slots.Save();
                _status = $"Ход передан: играет {target}";
                DualManagerPlugin.Logger.LogInfo(_status);
            }
            else
            {
                _status = "Не удалось передать ход: " + err;
                DualManagerPlugin.Logger.LogWarning(_status);
            }
        }

        // ── отрисовка: только Label и Box, каждый вызов защищён ──

        private void OnGUI()
        {
            if (!_open) return;
            try
            {
                GUI.depth = -10000;
                Draw();
            }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogError("Отрисовка: " + e.Message);
                _open = false;
            }
        }

        private void Box(Rect r)
        {
            if (_boxBroken) return;
            try { GUI.Box(r, ""); }
            catch { _boxBroken = true; }
        }

        private void Label(float x, float y, float w, string text)
        {
            try { GUI.Label(new Rect(x, y, w, 22f), text); }
            catch { }
        }

        private void Draw()
        {
            const float W = 640f, H = 520f, X = 40f, Y = 40f, LH = 22f;
            Box(new Rect(X, Y, W, H));

            float x = X + 12f, y = Y + 10f, w = W - 24f;

            Label(x, y, w, "=== ESM26 Dual Manager — две организации в одном мире ==="); y += LH + 4f;

            var currentName = GameBridge.TeamName(GameBridge.GetPlayerTeam());
            Label(x, y, w, $"Сейчас управляете: {currentName}"); y += LH;
            Label(x, y, w, $"Менеджер 1: {(_slots.OrgA == "" ? "не выбран" : _slots.OrgA)}"
                           + (_slots.Current == "A" ? "   <-- ходит" : "")); y += LH;
            Label(x, y, w, $"Менеджер 2: {(_slots.OrgB == "" ? "не выбран" : _slots.OrgB)}"
                           + (_slots.Current == "B" ? "   <-- ходит" : "")); y += LH + 6f;

            if (_picking == 0)
            {
                Label(x, y, w, "──────────────────────────────────────────"); y += LH;
                Label(x, y, w, "[1] выбрать организацию менеджера 1"); y += LH;
                Label(x, y, w, "[2] выбрать организацию менеджера 2"); y += LH;
                Label(x, y, w, "[Q] / [W] назначить текущую как менеджера 1 / 2"); y += LH;
                Label(x, y, w, $"[{DualManagerPlugin.SwapKey.Value}] передать ход   [R] найти организации"); y += LH;
                Label(x, y, w, "[T] расширенный поиск (риск подвисания)   [U] снять блокировку"); y += LH;
                Label(x, y, w, $"[{DualManagerPlugin.DumpKey.Value}] диагностика в файл   [Esc] закрыть"); y += LH + 6f;
            }
            else
            {
                var list = Filtered();
                if (_cursor >= list.Count) _cursor = Math.Max(0, list.Count - 1);

                Label(x, y, w, $"Организация для менеджера {_picking}   |   найдено: {list.Count}"); y += LH;
                Label(x, y, w, string.IsNullOrEmpty(_filter)
                    ? "Поиск: (просто печатайте название)"
                    : "Поиск: " + _filter + "_"); y += LH;
                Label(x, y, w, "Стрелки — выбор, Enter — назначить, Esc — отмена"); y += LH + 4f;

                if (list.Count == 0)
                {
                    Label(x, y, w, _teams.Count == 0
                        ? "Список пуст. Esc, затем R. Если пусто — T (расширенный), затем F9."
                        : "Ничего не найдено по фильтру.");
                    y += LH;
                }
                else
                {
                    if (_scrollTop > Math.Max(0, list.Count - ROWS)) _scrollTop = Math.Max(0, list.Count - ROWS);
                    int last = Math.Min(_scrollTop + ROWS, list.Count);

                    if (_scrollTop > 0) { Label(x, y, w, "   ▲ ещё выше"); y += LH; }

                    for (int i = _scrollTop; i < last; i++)
                    {
                        var nm = GameBridge.TeamName(list[i]);
                        Label(x, y, w, (i == _cursor ? " >> " : "    ") + nm);
                        y += LH;
                    }

                    if (last < list.Count) { Label(x, y, w, $"   ▼ ещё {list.Count - last}"); y += LH; }
                }
            }

            float footY = Y + H - 30f;
            Label(x, footY, w, _status ?? "");
        }
    }
}
