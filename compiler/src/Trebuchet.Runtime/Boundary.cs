using System.Collections;

namespace Trebuchet.Runtime;

/// <summary>
/// Conversions at the host boundary. Generated extern wrappers pass every host result
/// through <see cref="To{T}"/>, so a host may return arrays, lists, dictionaries, nullable
/// references, or Trebuchet types and the Trebuchet side sees Vector, Map, Option, and
/// plain values. Values crossing the other way are already IReadOnlyList and
/// IReadOnlyDictionary, so host signatures accept them without conversion.
/// </summary>
public static class Boundary
{
    public static T To<T>(object? value)
    {
        var target = typeof(T);
        if (value is T already && !target.IsGenericType) return already;
        if (target.IsGenericType)
        {
            var def = target.GetGenericTypeDefinition();
            var args = target.GetGenericArguments();
            if (def == typeof(Option<>))
            {
                if (value is null) return (T)Activator.CreateInstance(typeof(None<>).MakeGenericType(args))!;
                if (value is T asOption) return asOption;
                var inner = typeof(Boundary).GetMethod(nameof(To))!.MakeGenericMethod(args[0]).Invoke(null, new[] { value });
                return (T)Activator.CreateInstance(typeof(Some<>).MakeGenericType(args), inner)!;
            }
            if (value is T ok) return ok;
            if (def == typeof(Vector<>) && value is IEnumerable items)
            {
                var elem = args[0];
                var convert = typeof(Boundary).GetMethod(nameof(To))!.MakeGenericMethod(elem);
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elem))!;
                foreach (var item in items) list.Add(convert.Invoke(null, new[] { item }));
                return (T)target.GetMethod("From")!.Invoke(null, new object[] { list })!;
            }
            if (def == typeof(Set<>) && value is IEnumerable setItems)
            {
                var elem = args[0];
                var convert = typeof(Boundary).GetMethod(nameof(To))!.MakeGenericMethod(elem);
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elem))!;
                foreach (var item in setItems) list.Add(convert.Invoke(null, new[] { item }));
                return (T)target.GetMethod("From")!.Invoke(null, new object[] { list })!;
            }
            if (def == typeof(Map<,>) && value is IDictionary dict)
            {
                var pairType = typeof(KeyValuePair<,>).MakeGenericType(args);
                var pairs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pairType))!;
                var convertK = typeof(Boundary).GetMethod(nameof(To))!.MakeGenericMethod(args[0]);
                var convertV = typeof(Boundary).GetMethod(nameof(To))!.MakeGenericMethod(args[1]);
                foreach (DictionaryEntry e in dict)
                    pairs.Add(Activator.CreateInstance(pairType, convertK.Invoke(null, new[] { e.Key }), convertV.Invoke(null, new[] { e.Value })));
                return (T)target.GetMethod("From")!.Invoke(null, new object[] { pairs })!;
            }
        }
        if (value is null) throw new TrebPanic($"the host returned null where {target.Name} was expected; declare the extern as returning Option");
        if (target == typeof(long) && value is IConvertible c && value is not string) return (T)(object)c.ToInt64(null);
        if (target == typeof(double) && value is IConvertible d && value is not string) return (T)(object)d.ToDouble(null);
        if (target == typeof(DateTimeOffset) && value is DateTime dt) return (T)(object)new DateTimeOffset(dt.ToUniversalTime());
        if (value is T t) return t;
        throw new TrebPanic($"the host returned {value.GetType().Name} where {target.Name} was expected");
    }
}
