using System.Reflection;

namespace RbxMulti
{
    public static class AppVersion
    {
        public static string Display
        {
            get
            {
                Assembly assembly = typeof(AppVersion).Assembly;
                object[] attributes = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attributes.Length > 0) return ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
                return assembly.GetName().Version.ToString(3);
            }
        }
    }
}
