using UnityEditor;

namespace CorgiCabal.InspectorHistory
{
    sealed class EditorPrefBool
    {
        bool _fetched;
        readonly string _name;
        bool _val;

        public EditorPrefBool(bool defaultVal, string name) { _val = defaultVal; _name = name; }

        public bool GetVal() { Fetch(); return _val; }

        public void SetVal(bool v) { Fetch(); if (_val != v) { EditorPrefs.SetBool(_name, v); _val = v; } }

        void Fetch() { if (!_fetched) { _val = EditorPrefs.GetBool(_name, _val); _fetched = true; } }
    }

    sealed class EditorPrefInt
    {
        bool _fetched;
        readonly string _name;
        int _val;

        public EditorPrefInt(int defaultVal, string name) { _val = defaultVal; _name = name; }

        public int GetVal() { Fetch(); return _val; }

        public void SetVal(int v) { Fetch(); if (_val != v) { EditorPrefs.SetInt(_name, v); _val = v; } }

        void Fetch() { if (!_fetched) { _val = EditorPrefs.GetInt(_name, _val); _fetched = true; } }
    }
}
