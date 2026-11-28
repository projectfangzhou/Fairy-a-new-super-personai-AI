using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Startup.Tests;

/// <summary>
/// Reads a method's IL and lists the methods it calls.
///
/// This exists because some facts cannot be checked any other way without starting the desktop app:
/// "does startup actually call the launch rule" and "does shutdown actually dispose the server" are
/// wiring, and wiring that is only known to compile is exactly what breaks silently. Reading the IL
/// answers those questions with the shipped binary as the evidence.
/// </summary>
internal static class IlScanner
{
    private static readonly OpCode?[] OneByte = new OpCode?[256];
    private static readonly OpCode?[] TwoByte = new OpCode?[256];

    static IlScanner()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op) continue;

            var value = unchecked((ushort)op.Value);
            if (op.Size == 1) OneByte[value] = op;
            else if ((value & 0xFF00) == 0xFE00) TwoByte[value & 0xFF] = op;
        }
    }

    public static IReadOnlyList<MethodBase> CalledMethods(MethodBase method)
    {
        var calls = new List<MethodBase>();
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) return calls;

        var position = 0;
        while (position < il.Length)
        {
            OpCode? op;
            if (il[position] == 0xFE)
            {
                if (position + 1 >= il.Length) break;
                op = TwoByte[il[position + 1]];
                position += 2;
            }
            else
            {
                op = OneByte[il[position]];
                position += 1;
            }

            if (op is null) break;

            var size = OperandSize(op.Value, il, position);

            if (op.Value.OperandType is OperandType.InlineMethod && position + 4 <= il.Length)
            {
                var token = BitConverter.ToInt32(il, position);
                try
                {
                    var resolved = method.Module.ResolveMethod(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method.GetGenericArguments());
                    if (resolved is not null) calls.Add(resolved);
                }
                catch (ArgumentException)
                {
                    // Not a method token for this module (should not happen); skip rather than throw.
                }
            }

            position += size;
        }

        return calls;
    }

    /// <summary>The method body that actually runs for an <c>async</c> method.</summary>
    public static MethodBase? AsyncBody(MethodBase method)
    {
        var attribute = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        return attribute?.StateMachineType.GetMethod("MoveNext",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static int OperandSize(OpCode op, byte[] il, int position) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4
    };
}
