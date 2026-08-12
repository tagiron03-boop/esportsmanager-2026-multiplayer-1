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

        public override void Load()
        {
            Logger = Log;

            PanelKey = Config.Bind("Hotkeys", "PanelKey", KeyCode.F10,
                "Клавиша открытия панели Dual Manager");
            SwapKey = Config.Bind("Hotkeys", "SwapKey", KeyCode.F11,
                "Клавиша быстрой передачи хода между менеджерами");

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
        public static List<object> AllTeams()
        {
            var result = new List<object>();
            if (!Ready) return result;

            object container = null;

            if (_dataTeams != null)
            {
                var inst = FindMember(_dataTeams, "Instance", "instance", "Current");
                container = inst != null ? GetValue(inst, null) : null;
                if (container == null)
                {
                    var m2 = FindMember(_globalValues, "DataTeams", "dataTeams", "Teams", "teams");
                    container = m2 != null ? GetValue(m2, null) : null;
                }
            }
            if (container == null)
            {
                var m3 = FindMember(_globalValues, "Teams", "teams", "AllTeams", "DataTeams", "dataTeams");
                container = m3 != null ? GetValue(m3, null) : null;
            }
            if (container == null) return result;

            // Сам контейнер может быть списком или содержать список внутри.
            if (!TryEnumerate(container, result))
            {
                foreach (var name in new[] { "Teams", "teams", "List", "list", "Items", "items", "All", "all" })
                {
                    var m = FindMember(container.GetType(), name);
                    if (m == null) continue;
                    var inner = GetValue(m, container);
                    if (inner != null && TryEnumerate(inner, result)) break;
                }
            }
            return result;
        }

        private static bool TryEnumerate(object src, List<object> into)
        {
            try
            {
                if (src is System.Collections.IEnumerable en && !(src is string))
                {
                    foreach (var x in en) if (x != null) into.Add(x);
                    return into.Count > 0;
                }
                var t = src.GetType();
                var count = FindMember(t, "Count", "count", "Length");
                var idx = t.GetProperty("Item");
                if (count != null && idx != null)
                {
                    int n = Convert.ToInt32(GetValue(count, src));
                    for (int i = 0; i < n; i++)
                    {
                        var x = idx.GetValue(src, new object[] { i });
                        if (x != null) into.Add(x);
                    }
                    return into.Count > 0;
                }
            }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogWarning($"Не удалось перечислить организации: {e.Message}");
            }
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
        private Rect _win = new Rect(80, 80, 560, 520);
        private Vector2 _scroll;
        private string _filter = "";
        private Slots _slots;
        private List<object> _teams = new List<object>();
        private string _status = "";
        private int _picking; // 0 — нет, 1 — выбираем оргу A, 2 — оргу B

        private void Start()
        {
            _slots = Slots.Load();
        }

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(DualManagerPlugin.PanelKey.Value))
                {
                    _open = !_open;
                    if (_open) RefreshTeams();
                }

                if (Input.GetKeyDown(DualManagerPlugin.SwapKey.Value))
                    SwapTurn();
            }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogError($"Ошибка обработки клавиш: {e}");
            }
        }

        private void RefreshTeams()
        {
            _teams = GameBridge.AllTeams();
            _status = _teams.Count > 0
                ? $"Найдено организаций: {_teams.Count}"
                : "Организации не найдены — загрузите карьеру и откройте панель снова.";
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

        private void OnGUI()
        {
            if (!_open) return;
            try
            {
                // GUILayout.Window в IL2CPP опирается на конструктор, которого
                // в этой версии Unity нет, поэтому рисуем панель напрямую.
                GUI.Box(_win, "");
                GUILayout.BeginArea(new Rect(_win.x + 8, _win.y + 8, _win.width - 16, _win.height - 16));
                GUILayout.Label("ESM26 Dual Manager — две организации в одном мире");
                DrawWindow(0);
                GUILayout.EndArea();
            }
            catch (Exception e)
            {
                DualManagerPlugin.Logger.LogError($"Ошибка отрисовки панели: {e}");
                _open = false;
            }
        }

        private void DrawWindow(int id)
        {
            var current = GameBridge.GetPlayerTeam();
            var currentName = GameBridge.TeamName(current);

            GUILayout.BeginVertical("box");
            GUILayout.Label($"Сейчас управляете: {currentName}");
            GUILayout.Label($"Менеджер 1: {(string.IsNullOrEmpty(_slots.OrgA) ? "не выбран" : _slots.OrgA)}" +
                            (_slots.Current == "A" ? "   ◀ ходит" : ""));
            GUILayout.Label($"Менеджер 2: {(string.IsNullOrEmpty(_slots.OrgB) ? "не выбран" : _slots.OrgB)}" +
                            (_slots.Current == "B" ? "   ◀ ходит" : ""));
            GUILayout.EndVertical();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Назначить менеджера 1")) { _picking = 1; RefreshTeams(); }
            if (GUILayout.Button("Назначить менеджера 2")) { _picking = 2; RefreshTeams(); }
            if (GUILayout.Button($"⇄ Передать ход ({DualManagerPlugin.SwapKey.Value})")) SwapTurn();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Взять текущую как менеджера 1", GUILayout.Width(240)))
            {
                _slots.OrgA = currentName; _slots.Current = "A"; _slots.Save();
                _status = $"Менеджер 1 = {currentName}";
            }
            if (GUILayout.Button("Взять текущую как менеджера 2", GUILayout.Width(240)))
            {
                _slots.OrgB = currentName; _slots.Save();
                _status = $"Менеджер 2 = {currentName}";
            }
            GUILayout.EndHorizontal();

            if (_picking != 0)
            {
                GUILayout.Space(6);
                GUILayout.Label($"Выберите организацию для менеджера {_picking}:");
                GUILayout.BeginHorizontal();
                GUILayout.Label("Поиск:", GUILayout.Width(50));
                _filter = GUILayout.TextField(_filter ?? "");
                if (GUILayout.Button("Отмена", GUILayout.Width(80))) { _picking = 0; _filter = ""; }
                GUILayout.EndHorizontal();

                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(300));
                int shown = 0;
                foreach (var t in _teams)
                {
                    var nm = GameBridge.TeamName(t);
                    if (!string.IsNullOrEmpty(_filter) &&
                        nm.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (++shown > 300) break;

                    if (GUILayout.Button(nm))
                    {
                        if (_picking == 1) { _slots.OrgA = nm; _slots.Current = "A"; }
                        else { _slots.OrgB = nm; }
                        _slots.Save();
                        _status = $"Менеджер {_picking} = {nm}";
                        _picking = 0; _filter = "";
                        break;
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(4);
            GUILayout.Label(_status ?? "");
            GUILayout.Label($"Панель: {DualManagerPlugin.PanelKey.Value}   |   Передача хода: {DualManagerPlugin.SwapKey.Value}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Закрыть", GUILayout.Width(100))) _open = false;
            GUILayout.EndHorizontal();
        }
    }
}
