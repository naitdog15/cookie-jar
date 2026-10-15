using System;
using System.Linq;
using System.Reflection;

namespace CookieJar.Tests
{
    /// <summary>
    /// Tiny test runner shared by every test project (linked in, no NuGet packages). Add a
    /// "public static class XxxTests { public static void Run() { ... T.Check(...) ... } }" anywhere in a test
    /// project and RunAll finds it by reflection, so tasks never have to edit Program.cs.
    /// </summary>
    public static class T
    {
        private static int _pass;
        private static int _fail;

        public static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
        }

        public static int RunAll()
        {
            Assembly asm = typeof(T).Assembly;
            foreach (Type t in asm.GetTypes().Where(x => x.IsClass && x.IsAbstract && x.IsSealed && x.Name.EndsWith("Tests")).OrderBy(x => x.FullName))
            {
                MethodInfo m = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null) continue;
                Console.WriteLine("=== " + t.Name + " ===");
                try { m.Invoke(null, null); }
                catch (TargetInvocationException ex) { Check(t.Name + " threw", false, ex.InnerException.GetType().Name + ": " + ex.InnerException.Message); }
            }
            Console.WriteLine();
            Console.WriteLine("RESULT: " + _pass + " passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }
    }
}
