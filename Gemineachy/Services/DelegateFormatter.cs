using System.Reflection;
using System.Text;

namespace Gemineachy.Services
{
    public static class DelegateFormatter
    {
        public static string GetCsharpSignature(MethodInfo method)
        {
            // Get the underlying MethodInfo of the target method or the Invoke method of the delegate type
            if (method == null) return "unknown";

            var sb = new StringBuilder();

            // Return type
            sb.Append(GetFriendlyTypeName(method.ReturnType));
            sb.Append(" ");

            // Method Name
            sb.Append(method.Name);

            // Parameters
            sb.Append("(");
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (i > 0) sb.Append(", ");

                // Handle modifiers like out / ref / in
                if (p.IsOut) sb.Append("out ");
                else if (p.ParameterType.IsByRef) sb.Append("ref ");

                sb.Append($"{GetFriendlyTypeName(p.ParameterType.GetElementType() ?? p.ParameterType)} {p.Name}");
            }
            sb.Append(")");

            return sb.ToString();
        }
        public static string GetCsharpSignature(Delegate del)
        {
            // Get the underlying MethodInfo of the target method or the Invoke method of the delegate type
            MethodInfo? method = del.Method ?? del.GetType().GetMethod("Invoke");
            if (method == null) return "unknown";

            var sb = new StringBuilder();

            // Return type
            sb.Append(GetFriendlyTypeName(method.ReturnType));
            sb.Append(" ");

            // Method Name
            sb.Append(method.Name);

            // Parameters
            sb.Append("(");
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (i > 0) sb.Append(", ");

                // Handle modifiers like out / ref / in
                if (p.IsOut) sb.Append("out ");
                else if (p.ParameterType.IsByRef) sb.Append("ref ");

                sb.Append($"{GetFriendlyTypeName(p.ParameterType.GetElementType() ?? p.ParameterType)} {p.Name}");
            }
            sb.Append(")");

            return sb.ToString();
        }

        public static string GetFriendlyTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(long)) return "long";
            if (type == typeof(double)) return "double";
            if (type == typeof(float)) return "float";
            if (type == typeof(object)) return "object";

            // Handle Generics nicely (e.g., List<T>)
            if (type.IsGenericType)
            {
                var name = type.Name.Substring(0, type.Name.IndexOf('`'));
                var args = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
                return $"{name}<{args}>";
            }

            return type.Name;
        }
    }
}
