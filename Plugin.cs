using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        public const string VERSION = "1.0.0";

        internal static ManualLogSource Logger;
        internal static ConfigEntry<KeyCode> PanelKey;
        internal static ConfigEntry<KeyCode> SwapKey;
        internal static ConfigEntry<KeyCode> DumpKey;

        public override void Load()
        {
            Logger = Log;

            PanelKey = Config.Bind("Hotkeys", "PanelKey", KeyCode.F10,
                "Клавиша открытия панели Dual Manager");
            SwapKey = Config.Bind("Hotkeys", "SwapKey", KeyCode.F11,
                "Клавиша быстрой передачи хода между менеджерами");
            DumpKey = Config.Bind("Hotkeys", "DumpKey", KeyCode.F9,
                "Клавиша диагностики: выводит в лог структуру игровых данных");

            // В IL2CPP свой MonoBehaviour нужно сначала зарегистрировать в интеропе.
            ClassInjector.RegisterTypeInIl2Cpp<DualManagerUI>();

            var host = new GameObject("ESM26_DualManager");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<DualManagerUI>();

            Logger.LogInfo($"{NAME} v{VERSION} загружен. Панель: {PanelKey.Value}, передача хода: {SwapKey.Value}");
        }
    }

    /// <summary>
    /// Доступ к игровым объектам через рефлексию.
    /// Так плагин не требует ссылок на interop-сборки при компиляции —
    /// они генерируются BepInEx только на машине игрока.
    /// </summary>
    internal static class GameBridge
    {
        private static Type _globalValues;
        private static Type _dataTeam;
        private static Type _dataTeams;
        private static bool _searched;

        public static bool Ready => Resolve();

        private static bool Resolve()
        {
            if (_searched) return _globalValues != null && _dataTeam != null;
            _searched = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name;
                if (n != "Assembly-CSharp" && n != "EsportsManager" &&
                    !n.StartsWith("Il2Cpp", StringComparison.Ordinal)) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    switch (t.Name)
                    {
                        case "GlobalValues": _globalValues ??= t; break;
                        case "DataTeam": _dataTeam ??= t; break;
                        case "DataTeams": _dataTeams ??= t; break;
                    }
                }
                if (_globalValues != null && _dataTeam != null && _dataTeams != null) break;
            }

            if (_globalValues == null)
                DualManagerPlugin.Logger.LogWarning("Не найден тип GlobalValues — игра ещё не загрузила данные?");
            return _globalValues != null && _dataTeam != null;
        }

        private static MemberInfo FindMember(Type t, params string[] names)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.Instance;
            foreach (var n in names)
            {
                var p = t.GetProperty(n, F);
                if (p != null) return p;
                var f = t.GetField(n, F);
                if (f != null) return f;
            }
            return null;
        }

        private static object GetValue(MemberInfo m, object inst)
        {
            if (m is PropertyInfo p) return p.GetValue(inst);
            if (m is FieldInfo f) return f.GetValue(inst);
            return null;
        }

        private static void SetValue(MemberInfo m, object inst, object val)
        {
            if (m is PropertyInfo p) { p.SetValue(inst, val); return; }
            if (m is FieldInfo f) { f.SetValue(inst, val); }
        }

        /// <summary>Текущая управляемая организация.</summary>
        public static object GetPlayerTeam()
        {
            if (!Ready) return null;
            var m = FindMember(_globalValues, "DataTeam", "dataTeam", "PlayerTeam", "playerTeam");
            return m == null ? null : GetValue(m, null);
        }

        public static bool SetPlayerTeam(object team)
        {
            if (!Ready || team == null) return false;
            var m = FindMember(_globalValues, "DataTeam", "dataTeam", "PlayerTeam", "playerTeam");
            if (m == null) return false;
            try { SetValue(m, null, team); return true; }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogError($"Не удалось сменить организацию: {e.Message}");
                return false;
            }
        }

        public static string TeamName(object team)
        {
            if (team == null) return "—";
            var m = FindMember(team.GetType(), "Name", "name", "TeamName", "teamName", "ShortName");
            var v = m == null ? null : GetValue(m, team);
            return v?.ToString() ?? team.ToString();
        }

        /// <summary>Список всех организаций мира.</summary>
        /// <summary>
        /// Пишет в лог все члены найденных игровых типов.
        /// Нужно, чтобы определить настоящие имена полей в этой версии игры.
        /// </summary>
        public static void DumpMembers()
        {
            var log = DualManagerPlugin.Logger;
            if (!Resolve())
            {
                log.LogWarning("DUMP: игровые типы не найдены.");
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    log.LogInfo("DUMP asm: " + asm.GetName().Name);
                return;
            }

            const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags IF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var teamType = _dataTeam;
            log.LogInfo($"DUMP DataTeam type = {teamType?.FullName}");
            var cur = GetPlayerTeam();
            log.LogInfo($"DUMP playerTeam = {(cur == null ? "null" : cur.GetType().FullName)}");

            int typesScanned = 0, printed = 0;
            foreach (var t in GameTypes())
            {
                typesScanned++;
                var nm = t.Name;
                bool interesting =
                    nm.IndexOf("Team", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.EndsWith("Database", StringComparison.Ordinal) ||
                    nm == "GlobalValues";
                if (!interesting) continue;
                if (printed++ > 40) break;

                log.LogInfo($"DUMP === {t.FullName} ===");
                try
                {
                    foreach (var m in StaticMembers(t, SF))
                    {
                        var mt = m is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)m).FieldType;
                        var val = SafeGet(m, null);
                        string extra = "";
                        if (val != null)
                        {
                            var probe = new List<object>();
                            if (TryEnumerate(val, probe))
                                extra = $"  -> коллекция, {probe.Count} шт." +
                                        (probe.Count > 0 ? $", элемент {probe[0].GetType().Name}" : "");
                            else extra = "  -> значение есть";
                        }
                        log.LogInfo($"DUMP   static {mt.Name} {m.Name}{extra}");
                    }

                    var instM = FindMember(t, "Instance", "instance", "Current", "current");
                    var inst = instM != null ? SafeGet(instM, null) : null;
                    if (inst != null)
                    {
                        log.LogInfo($"DUMP   -- Instance: {inst.GetType().Name} --");
                        foreach (var m in InstanceMembers(inst.GetType(), IF))
                        {
                            var mt = m is PropertyInfo pi2 ? pi2.PropertyType : ((FieldInfo)m).FieldType;
                            var val = SafeGet(m, inst);
                            string extra = "";
                            if (val != null)
                            {
                                var probe = new List<object>();
                                if (TryEnumerate(val, probe))
                                    extra = $"  -> коллекция, {probe.Count} шт." +
                                            (probe.Count > 0 ? $", элемент {probe[0].GetType().Name}" : "");
                            }
                            log.LogInfo($"DUMP   inst {mt.Name} {m.Name}{extra}");
                        }
                    }
                }
                catch (Exception e) { log.LogWarning($"DUMP {t.Name}: {e.Message}"); }
            }
            log.LogInfo($"DUMP: типов просмотрено {typesScanned}, выведено {printed}");
        }

        private static Func<List<object>> _teamsAccessor;
        private static string _accessorDesc = "не найден";
        public static string AccessorDescription => _accessorDesc;

        public static List<object> AllTeams()
        {
            var result = new List<object>();
            if (!Ready) return result;

            // Найденный однажды путь к списку переиспользуется.
            if (_teamsAccessor != null)
            {
                try
                {
                    var cached = _teamsAccessor();
                    if (cached != null && cached.Count > 0) return cached;
                }
                catch { _teamsAccessor = null; }
            }

            if (FindTeamsAccessor(result)) return result;

            DualManagerPlugin.Logger.LogWarning(
                "Список организаций не найден ни в одном игровом типе. " +
                "Нажмите клавишу диагностики и пришлите лог.");
            return result;
        }

        /// <summary>
        /// Полный обход игровых типов в поисках коллекции организаций:
        /// сначала статические члены, затем содержимое синглтонов (Instance).
        /// </summary>
        private static bool FindTeamsAccessor(List<object> result)
        {
            const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags IF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var log = DualManagerPlugin.Logger;

            foreach (var type in GameTypes())
            {
                if (sw.ElapsedMilliseconds > 8000) { log.LogWarning("Поиск списка прерван по времени."); break; }

                // 1. Статические поля и свойства самого типа
                foreach (var m in StaticMembers(type, SF))
                {
                    var v = SafeGet(m, null);
                    if (v == null) continue;

                    var tmp = new List<object>();
                    if (TryExtractTeams(v, tmp))
                    {
                        var capturedType = type; var capturedMember = m;
                        _teamsAccessor = () =>
                        {
                            var list = new List<object>();
                            var val = SafeGet(capturedMember, null);
                            if (val != null) TryExtractTeams(val, list);
                            return list;
                        };
                        _accessorDesc = $"{capturedType.Name}.{capturedMember.Name}";
                        log.LogInfo($"Список организаций найден: {_accessorDesc} ({tmp.Count} шт.)");
                        result.AddRange(tmp);
                        return true;
                    }
                }

                // 2. Синглтон: Type.Instance -> его поля и свойства
                MemberInfo instMember = FindMember(type, "Instance", "instance", "Current", "current", "Singleton");
                if (instMember == null) continue;
                var inst = SafeGet(instMember, null);
                if (inst == null) continue;

                foreach (var m in InstanceMembers(inst.GetType(), IF))
                {
                    var v = SafeGet(m, inst);
                    if (v == null) continue;

                    var tmp = new List<object>();
                    if (TryExtractTeams(v, tmp))
                    {
                        var capturedInstM = instMember; var capturedM = m; var capturedType = type;
                        _teamsAccessor = () =>
                        {
                            var list = new List<object>();
                            var i2 = SafeGet(capturedInstM, null);
                            if (i2 == null) return list;
                            var val = SafeGet(capturedM, i2);
                            if (val != null) TryExtractTeams(val, list);
                            return list;
                        };
                        _accessorDesc = $"{capturedType.Name}.Instance.{capturedM.Name}";
                        log.LogInfo($"Список организаций найден: {_accessorDesc} ({tmp.Count} шт.)");
                        result.AddRange(tmp);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Игровые типы, отсортированные так, чтобы вероятные шли первыми.</summary>
        private static List<Type> GameTypes()
        {
            var list = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name;
                if (n != "Assembly-CSharp" && n != "EsportsManager" &&
                    !n.StartsWith("Il2Cpp", StringComparison.Ordinal)) continue;
                if (n == "Il2Cppmscorlib" || n == "Il2CppSystem.Core") continue;

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
                if (n.IndexOf("Team", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
                if (n.EndsWith("Database", StringComparison.Ordinal)) return 2;
                if (n == "GlobalValues") return 3;
                if (n.EndsWith("Manager", StringComparison.Ordinal) ||
                    n.EndsWith("Engine", StringComparison.Ordinal)) return 4;
                if (n.StartsWith("Data", StringComparison.Ordinal)) return 5;
                return 9;
            }
            list.Sort((x, y) => Score(x).CompareTo(Score(y)));
            return list;
        }

        private static List<MemberInfo> StaticMembers(Type t, BindingFlags f)
        {
            var res = new List<MemberInfo>();
            try
            {
                foreach (var fi in t.GetFields(f)) res.Add(fi);
                foreach (var pi in t.GetProperties(f))
                    if (pi.GetIndexParameters().Length == 0 && pi.CanRead) res.Add(pi);
            }
            catch { }
            return res;
        }

        private static List<MemberInfo> InstanceMembers(Type t, BindingFlags f)
        {
            var res = new List<MemberInfo>();
            try
            {
                foreach (var fi in t.GetFields(f)) res.Add(fi);
                foreach (var pi in t.GetProperties(f))
                    if (pi.GetIndexParameters().Length == 0 && pi.CanRead) res.Add(pi);
            }
            catch { }
            return res;
        }

        private static object SafeGet(MemberInfo m, object inst)
        {
            try { return GetValue(m, inst); }
            catch { return null; }
        }

        private static bool TryExtractTeams(object src, List<object> into)
        {
            if (src == null) return false;

            var tmp = new List<object>();
            if (TryEnumerate(src, tmp) && LooksLikeTeams(tmp))
            {
                into.AddRange(tmp);
                return true;
            }

            // Список может лежать внутри объекта-контейнера
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = src.GetType();
            foreach (var f in t.GetFields(F))
            {
                var v = SafeGet(f, src);
                if (v == null) continue;
                tmp.Clear();
                if (TryEnumerate(v, tmp) && LooksLikeTeams(tmp)) { into.AddRange(tmp); return true; }
            }
            foreach (var p in t.GetProperties(F))
            {
                var v = SafeGet(p, src);
                if (v == null) continue;
                tmp.Clear();
                if (TryEnumerate(v, tmp) && LooksLikeTeams(tmp)) { into.AddRange(tmp); return true; }
            }
            return false;
        }

        /// <summary>Проверяет, что коллекция похожа на список организаций.</summary>
        private static bool LooksLikeTeams(List<object> items)
        {
            if (items.Count < 3) return false;
            var first = items[0];
            if (first == null) return false;

            // Тип элемента должен совпадать с типом команды игрока
            if (_dataTeam != null && _dataTeam.IsInstanceOfType(first)) return true;

            var tn = first.GetType().Name;
            return tn.IndexOf("Team", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Перечисляет коллекцию. Учитывает, что в IL2CPP списки — это
        /// Il2CppSystem.Collections.Generic.List&lt;T&gt; и Il2CppReferenceArray&lt;T&gt;,
        /// которые не реализуют обычный System.Collections.IEnumerable.
        /// </summary>
        private static bool TryEnumerate(object src, List<object> into)
        {
            if (src == null || src is string) return false;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = src.GetType();

            // Примитивы и структуры коллекциями не бывают.
            if (t.IsPrimitive || t.IsEnum) return false;

            // 1. Обычный managed IEnumerable
            try
            {
                if (src is System.Collections.IEnumerable en)
                {
                    foreach (var x in en) if (x != null) into.Add(x);
                    if (into.Count > 0) return true;
                }
            }
            catch { into.Clear(); }

            // 2. Count/Length + индексатор Item либо метод get_Item
            try
            {
                var countM = FindMember(t, "Count", "count", "Length", "length", "Size");
                if (countM != null)
                {
                    var cv = SafeGet(countM, src);
                    if (cv != null)
                    {
                        int n = Convert.ToInt32(cv);
                        if (n > 0 && n < 20000)
                        {
                            PropertyInfo idxProp = null;
                            foreach (var p in t.GetProperties(F))
                            {
                                var ps = p.GetIndexParameters();
                                if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) { idxProp = p; break; }
                            }
                            MethodInfo getItem = null;
                            if (idxProp == null)
                            {
                                foreach (var mi in t.GetMethods(F))
                                {
                                    if (mi.Name != "get_Item" && mi.Name != "Get") continue;
                                    var ps = mi.GetParameters();
                                    if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) { getItem = mi; break; }
                                }
                            }

                            if (idxProp != null || getItem != null)
                            {
                                for (int i = 0; i < n; i++)
                                {
                                    object x = null;
                                    try
                                    {
                                        x = idxProp != null
                                            ? idxProp.GetValue(src, new object[] { i })
                                            : getItem.Invoke(src, new object[] { i });
                                    }
                                    catch { break; }
                                    if (x != null) into.Add(x);
                                }
                                if (into.Count > 0) return true;
                            }
                        }
                    }
                }
            }
            catch { into.Clear(); }

            // 3. Ручной обход через GetEnumerator (работает для Il2Cpp-списков)
            try
            {
                var getEnum = t.GetMethod("GetEnumerator", F, null, Type.EmptyTypes, null);
                if (getEnum != null)
                {
                    var e = getEnum.Invoke(src, null);
                    if (e != null)
                    {
                        var et = e.GetType();
                        var moveNext = et.GetMethod("MoveNext", F, null, Type.EmptyTypes, null);
                        var currentM = FindMember(et, "Current", "current");
                        if (moveNext != null && currentM != null)
                        {
                            int guard = 0;
                            while (guard++ < 20000)
                            {
                                object ok;
                                try { ok = moveNext.Invoke(e, null); } catch { break; }
                                if (!(ok is bool b) || !b) break;
                                var x = SafeGet(currentM, e);
                                if (x != null) into.Add(x);
                            }
                            if (into.Count > 0) return true;
                        }
                    }
                }
            }
            catch { into.Clear(); }

            // 4. ToArray()
            try
            {
                var toArr = t.GetMethod("ToArray", F, null, Type.EmptyTypes, null);
                if (toArr != null)
                {
                    var arr = toArr.Invoke(src, null);
                    if (arr != null && !ReferenceEquals(arr, src))
                        return TryEnumerate(arr, into);
                }
            }
            catch { into.Clear(); }

            into.Clear();
            return false;
        }
    }

    /// <summary>Сохраняемая привязка менеджеров к организациям.</summary>
    internal class Slots
    {
        public string OrgA = "";
        public string OrgB = "";
        public string Current = "A";

        private static string PathOf() =>
            System.IO.Path.Combine(Paths.ConfigPath, "esm26.dualmanager.slots.txt");

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
            catch (Exception e) { DualManagerPlugin.Logger.LogWarning($"Чтение настроек: {e.Message}"); }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(PathOf(), new[]
                {
                    "OrgA=" + OrgA,
                    "OrgB=" + OrgB,
                    "Current=" + Current
                });
            }
            catch (Exception e) { DualManagerPlugin.Logger.LogWarning($"Запись настроек: {e.Message}"); }
        }
    }

    public class DualManagerUI : MonoBehaviour
    {
        // Требуется инжектором Il2Cpp для создания управляемой обёртки.
        public DualManagerUI(IntPtr ptr) : base(ptr) { }

        private bool _open;
        private Vector2 _scroll;
        private string _filter = "";  // строка поиска, набирается с клавиатуры
        private Slots _slots;
        private List<object> _teams = new List<object>();
        private string _status = "";
        private int _picking; // 0 — нет, 1 — выбираем оргу A, 2 — оргу B
        private int _cursor;  // выделенная строка в списке (для клавиатуры)

        private void Start()
        {
            _slots = Slots.Load();

            // GUILayout вырезан из сборки игры. Unity по умолчанию гоняет для
            // OnGUI событие Layout через инфраструктуру GUILayout — из-за этого
            // ломается вся цепочка событий и кнопки не получают клики.
            try { useGUILayout = false; }
            catch (Exception e) { DualManagerPlugin.Logger.LogWarning($"useGUILayout: {e.Message}"); }
        }

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(DualManagerPlugin.PanelKey.Value))
                {
                    _open = !_open;
                    ApplyCursorState(_open);
                    if (_open) RefreshTeams();
                }

                if (Input.GetKeyDown(DualManagerPlugin.SwapKey.Value))
                    SwapTurn();

                if (Input.GetKeyDown(DualManagerPlugin.DumpKey.Value))
                {
                    GameBridge.DumpMembers();
                    _status = "Диагностика записана в BepInEx\\LogOutput.log";
                }

                if (_open) HandleKeyboard();
            }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogError($"Ошибка обработки клавиш: {e}");
            }
        }

        /// <summary>
        /// Полное управление с клавиатуры — работает даже если игра
        /// перехватывает мышь и кнопки не нажимаются.
        /// </summary>
        private void HandleKeyboard()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_picking != 0) { _picking = 0; _filter = ""; }
                else { _open = false; ApplyCursorState(false); }
                return;
            }

            // Выбор организации из списка
            if (_picking != 0)
            {
                var list = FilteredTeams();

                if (Input.GetKeyDown(KeyCode.DownArrow))
                    _cursor = list.Count == 0 ? 0 : Mathf.Min(_cursor + 1, list.Count - 1);
                if (Input.GetKeyDown(KeyCode.UpArrow))
                    _cursor = Mathf.Max(_cursor - 1, 0);
                if (Input.GetKeyDown(KeyCode.PageDown))
                    _cursor = list.Count == 0 ? 0 : Mathf.Min(_cursor + 10, list.Count - 1);
                if (Input.GetKeyDown(KeyCode.PageUp))
                    _cursor = Mathf.Max(_cursor - 10, 0);

                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (_cursor >= 0 && _cursor < list.Count)
                        AssignTeam(GameBridge.TeamName(list[_cursor]));
                    return;
                }

                // Набор строки поиска обычными клавишами
                var typed = Input.inputString;
                if (!string.IsNullOrEmpty(typed))
                {
                    foreach (var ch in typed)
                    {
                        if (ch == '\b')
                        {
                            if (_filter.Length > 0) _filter = _filter.Substring(0, _filter.Length - 1);
                        }
                        else if (ch != '\n' && ch != '\r')
                        {
                            _filter += ch;
                        }
                    }
                    _cursor = 0;
                }
                return;
            }

            // Основные действия
            if (Input.GetKeyDown(KeyCode.Alpha1)) { _picking = 1; _cursor = 0; RefreshTeams(); }
            if (Input.GetKeyDown(KeyCode.Alpha2)) { _picking = 2; _cursor = 0; RefreshTeams(); }

            if (Input.GetKeyDown(KeyCode.Q)) TakeCurrentAs(1);
            if (Input.GetKeyDown(KeyCode.W)) TakeCurrentAs(2);
            if (Input.GetKeyDown(KeyCode.R)) RefreshTeams();
        }

        private List<object> FilteredTeams()
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

        private void AssignTeam(string nm)
        {
            if (_picking == 1) { _slots.OrgA = nm; _slots.Current = "A"; }
            else if (_picking == 2) { _slots.OrgB = nm; }
            _slots.Save();
            _status = $"Менеджер {_picking} = {nm}";
            _picking = 0;
            _filter = "";
            _cursor = 0;
        }

        private void TakeCurrentAs(int slot)
        {
            var nm = GameBridge.TeamName(GameBridge.GetPlayerTeam());
            if (slot == 1) { _slots.OrgA = nm; _slots.Current = "A"; }
            else { _slots.OrgB = nm; }
            _slots.Save();
            _status = $"Менеджер {slot} = {nm}";
        }

        private void RefreshTeams()
        {
            _teams = GameBridge.AllTeams();
            _status = _teams.Count > 0
                ? $"Найдено организаций: {_teams.Count}  (источник: {GameBridge.AccessorDescription})"
                : "Организации не найдены. Загрузите карьеру, нажмите R. Если пусто — F9 и пришлите лог.";
        }

        private void SwapTurn()
        {
            if (string.IsNullOrEmpty(_slots.OrgA) || string.IsNullOrEmpty(_slots.OrgB))
            {
                _status = "Сначала назначьте обе организации в панели.";
                DualManagerPlugin.Logger.LogInfo(_status);
                return;
            }

            var target = _slots.Current == "A" ? _slots.OrgB : _slots.OrgA;
            if (_teams.Count == 0) RefreshTeams();

            var team = _teams.FirstOrDefault(t =>
                string.Equals(GameBridge.TeamName(t), target, StringComparison.OrdinalIgnoreCase));

            if (team == null)
            {
                _status = $"Организация «{target}» не найдена в текущем мире.";
                DualManagerPlugin.Logger.LogWarning(_status);
                return;
            }

            if (GameBridge.SetPlayerTeam(team))
            {
                _slots.Current = _slots.Current == "A" ? "B" : "A";
                _slots.Save();
                _status = $"Ход передан: играет {target}";
                DualManagerPlugin.Logger.LogInfo(_status);
            }
            else
            {
                _status = "Не удалось переключить организацию — смотрите лог.";
            }
        }

        private CursorLockMode _prevLock;
        private bool _prevCursorVisible;
        private bool _cursorSaved;
        private int _eventsSeen;

        private void OnGUI()
        {
            if (!_open) return;
            try
            {
                // Панель должна быть поверх интерфейса игры.
                GUI.depth = -10000;

                // Диагностика: какие события вообще доходят до панели.
                if (_eventsSeen < 12 && Event.current != null)
                {
                    var t = Event.current.type;
                    if (t == EventType.MouseDown || t == EventType.MouseUp)
                    {
                        _eventsSeen++;
                        DualManagerPlugin.Logger.LogInfo(
                            $"GUI event: {t} at {Event.current.mousePosition}");
                    }
                }

                DrawPanel();
            }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogError($"Ошибка отрисовки панели: {e}");
                _open = false;
            }
        }

        /// <summary>Пока панель открыта, курсор должен быть свободен и виден.</summary>
        private void ApplyCursorState(bool open)
        {
            try
            {
                if (open)
                {
                    if (!_cursorSaved)
                    {
                        _prevLock = Cursor.lockState;
                        _prevCursorVisible = Cursor.visible;
                        _cursorSaved = true;
                    }
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else if (_cursorSaved)
                {
                    Cursor.lockState = _prevLock;
                    Cursor.visible = _prevCursorVisible;
                    _cursorSaved = false;
                }
            }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogWarning($"Курсор: {e.Message}");
            }
        }

        // Только GUI с явными координатами: GUILayout вырезан из сборки игры
        // (Unity strip), его вызовы приводят к "Method unstripping failed".
        private void DrawPanel()
        {
            const float W = 620f, H = 560f;
            float x = 60f, y = 60f;

            GUI.Box(new Rect(x, y, W, H), "");

            float cx = x + 12f;
            float cy = y + 10f;
            const float LH = 22f;   // высота строки
            const float BH = 26f;   // высота кнопки

            GUI.Label(new Rect(cx, cy, W - 24f, LH),
                "ESM26 Dual Manager — две организации в одном мире");
            cy += LH + 6f;

            var current = GameBridge.GetPlayerTeam();
            var currentName = GameBridge.TeamName(current);

            GUI.Label(new Rect(cx, cy, W - 24f, LH),
                $"Сейчас управляете: {currentName}      Организаций найдено: {_teams.Count}");
            cy += LH;
            GUI.Label(new Rect(cx, cy, W - 24f, LH),
                $"Менеджер 1: {(string.IsNullOrEmpty(_slots.OrgA) ? "не выбран" : _slots.OrgA)}"
                + (_slots.Current == "A" ? "   <-- ходит" : ""));
            cy += LH;
            GUI.Label(new Rect(cx, cy, W - 24f, LH),
                $"Менеджер 2: {(string.IsNullOrEmpty(_slots.OrgB) ? "не выбран" : _slots.OrgB)}"
                + (_slots.Current == "B" ? "   <-- ходит" : ""));
            cy += LH + 8f;

            float bw = (W - 36f) / 2f;
            if (GUI.Button(new Rect(cx, cy, bw, BH), "Взять текущую как менеджера 1  [Q]"))
                TakeCurrentAs(1);
            if (GUI.Button(new Rect(cx + bw + 12f, cy, bw, BH), "Взять текущую как менеджера 2  [W]"))
                TakeCurrentAs(2);
            cy += BH + 6f;

            float bw3 = (W - 48f) / 3f;
            if (GUI.Button(new Rect(cx, cy, bw3, BH), "Выбрать оргу менеджера 1  [1]"))
            { _picking = 1; _cursor = 0; RefreshTeams(); }
            if (GUI.Button(new Rect(cx + bw3 + 12f, cy, bw3, BH), "Выбрать оргу менеджера 2  [2]"))
            { _picking = 2; _cursor = 0; RefreshTeams(); }
            if (GUI.Button(new Rect(cx + (bw3 + 12f) * 2f, cy, bw3, BH),
                    $"Передать ход ({DualManagerPlugin.SwapKey.Value})"))
                SwapTurn();
            cy += BH + 10f;

            if (_picking != 0)
            {
                GUI.Label(new Rect(cx, cy, 220f, LH), $"Организация для менеджера {_picking}:");
                // GUI.TextField вырезан из сборки игры, поэтому фильтр
                // набирается обычными клавишами и рисуется как текст.
                GUI.Box(new Rect(cx + 224f, cy, 260f, LH), "");
                GUI.Label(new Rect(cx + 230f, cy, 250f, LH),
                    string.IsNullOrEmpty(_filter) ? "Поиск: (просто печатайте)" : "Поиск: " + _filter);
                if (GUI.Button(new Rect(cx + 492f, cy, 92f, LH), "Отмена  [Esc]"))
                { _picking = 0; _filter = ""; }
                cy += LH + 6f;

                float listH = y + H - cy - 70f;
                var listRect = new Rect(cx, cy, W - 24f, listH);

                var matches = FilteredTeams();
                if (_cursor >= matches.Count) _cursor = Math.Max(0, matches.Count - 1);

                float rowH = 24f;
                var viewRect = new Rect(0f, 0f, W - 48f, matches.Count * rowH + 4f);
                _scroll = GUI.BeginScrollView(listRect, _scroll, viewRect);
                for (int i = 0; i < matches.Count; i++)
                {
                    var nm = GameBridge.TeamName(matches[i]);
                    var r = new Rect(2f, i * rowH, W - 70f, rowH - 2f);
                    if (i == _cursor)
                    {
                        GUI.Box(r, "");
                        nm = "> " + nm;
                    }
                    if (GUI.Button(r, nm))
                    {
                        AssignTeam(GameBridge.TeamName(matches[i]));
                        break;
                    }
                }
                GUI.EndScrollView();
                cy += listH + 6f;
            }

            float footY = y + H - 58f;
            GUI.Label(new Rect(cx, footY, W - 24f, LH), _status ?? "");
            GUI.Label(new Rect(cx, footY + LH, W - 130f, LH),
                _picking != 0
                    ? "Клавиши: стрелки — выбор, Enter — назначить, Esc — отмена"
                    : $"Клавиши: 1/2 — выбрать оргу, Q/W — взять текущую, {DualManagerPlugin.SwapKey.Value} — ход, {DualManagerPlugin.DumpKey.Value} — диагностика, Esc — закрыть");
            if (GUI.Button(new Rect(x + W - 224f, footY + LH - 2f, 106f, BH), "Диагностика"))
            {
                GameBridge.DumpMembers();
                _status = "Диагностика записана в BepInEx\\LogOutput.log";
            }
            if (GUI.Button(new Rect(x + W - 112f, footY + LH - 2f, 100f, BH), "Закрыть"))
                _open = false;
        }

    }
}
