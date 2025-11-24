using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using Sadie.API;
using Sadie.Core.Shared.Attributes;

namespace Sadie.Networking.Serialization;

public static class NetworkPacketWriterSerializer
{
    private static readonly Dictionary<Type, Action<object, NetworkPacketWriter>> primitiveWriters =
        new()
        {
            { typeof(string), (v, w) => w.WriteString((string)v) },
            { typeof(int), (v, w) => w.WriteInteger((int)v) },
            { typeof(short), (v, w) => w.WriteShort((short)v) },
            { typeof(long), (v, w) => w.WriteLong((long)v) },
            { typeof(bool), (v, w) => w.WriteBool((bool)v) }
        };

    private static void InvokeOnConfigureRules(object packet)
    {
        var configureRulesMethod = packet.GetType().GetMethod("OnConfigureRules");
        configureRulesMethod?.Invoke(packet, []);
    }
    
    private static bool InvokeOnSerializeIfExists(object packet, NetworkPacketWriter writer)
    {
        var onSerialize = packet.GetType().GetMethod("OnSerialize");

        if (onSerialize == null)
        {
            return false;
        }
        
        if (onSerialize.GetBaseDefinition().DeclaringType == onSerialize.DeclaringType)
        {
            return false;
        }
        
        onSerialize.Invoke(packet, [writer]);
        return true;
    }

    private static short GetPacketIdentifierFromAttribute(object packetObject)
    {
        var identifierAttribute = packetObject.GetType().GetCustomAttribute<PacketIdAttribute>();

        if (identifierAttribute == null)
        {
            throw new InvalidOperationException($"Missing packet identifier attribute for packet type {packetObject.GetType()}");
        }

        return identifierAttribute.Id;
    }
    
    private static Dictionary<PropertyInfo, Action<INetworkPacketWriter>> GetRuleMap(object classObject, string propertyName)
    {
        return (Dictionary<PropertyInfo, Action<INetworkPacketWriter>>) classObject
            .GetType()
            .BaseType?.GetProperty(propertyName)
            ?.GetValue(classObject)!;
    }

    private static Dictionary<PropertyInfo, Action<INetworkPacketWriter>> GetBeforeRuleMap(object classObject) => 
        GetRuleMap(classObject, "BeforeRulesSerialize");
    private static Dictionary<PropertyInfo, Action<INetworkPacketWriter>> GetInsteadRuleMap(object classObject) => 
        GetRuleMap(classObject, "InsteadRulesSerialize");

    private static Dictionary<PropertyInfo, Action<INetworkPacketWriter>> GetAfterRuleMap(object classObject) =>
        GetRuleMap(classObject, "AfterRulesSerialize");
    
    private static Dictionary<PropertyInfo, KeyValuePair<Type, Func<object, object>>> GetConversionRules(object classObject) => 
        (Dictionary<PropertyInfo, KeyValuePair<Type, Func<object, object>>>)
        classObject
            .GetType()
            .BaseType?.GetProperty("ConversionRules")
            ?.GetValue(classObject)!;

    private static bool TryWritePrimitive(PropertyInfo property, object packet, NetworkPacketWriter writer)
    {
        var type = property.PropertyType;
        if (!primitiveWriters.TryGetValue(type, out var action))
        {
            return false;
        }

        var value = property.GetValue(packet)!;
        action(value, writer);
        return true;
    }

    private static void WriteDictionary<TKey, TValue>(Dictionary<TKey, TValue> dict, NetworkPacketWriter writer, Action<TKey> keyWriter, Action<TValue> valueWriter)
    {
        writer.WriteInteger(dict.Count);

        foreach (var kv in dict)
        {
            keyWriter(kv.Key);
            valueWriter(kv.Value);
        }
    }
    
    private static void AddObjectToWriter(object packet, NetworkPacketWriter writer, bool needsAttribute = false)
    {
        var properties = packet
            .GetType()
            .GetProperties()
            .Where(p => !needsAttribute || Attribute.IsDefined(p, typeof(PacketDataAttribute)));
        
        var conversionRules = GetConversionRules(packet);        
        var beforeRuleMap = GetBeforeRuleMap(packet);
        var insteadRuleMap = GetInsteadRuleMap(packet);
        var afterRuleMap = GetAfterRuleMap(packet);

        foreach (var property in properties)
        {
            if (conversionRules != null && conversionRules.TryGetValue(property, out var rule))
            {
                WriteType(rule.Key, rule.Value.Invoke(property.GetValue(packet)!), writer);
                continue;
            }
            
            if (insteadRuleMap != null && insteadRuleMap.TryGetValue(property, out var value))
            {
                value.Invoke(writer);
                continue;
            }

            if (beforeRuleMap != null && beforeRuleMap.TryGetValue(property, out var beforeValue))
            {
                beforeValue.Invoke(writer);
            }
            
            WriteProperty(property, writer, packet);

            if (afterRuleMap != null && afterRuleMap.TryGetValue(property, out var afterValue))
            {
                afterValue.Invoke(writer);
            }
        }
    }
    
    public static INetworkPacketWriter Serialize(object packet)
    {
        var writer = new NetworkPacketWriter();
        
        writer.WriteShort(GetPacketIdentifierFromAttribute(packet));

        if (InvokeOnSerializeIfExists(packet, writer))
        {
            return writer;
        }
        
        InvokeOnConfigureRules(packet);
        AddObjectToWriter(packet, writer);

        return writer;
    }

    private static void WriteStringListPropertyToWriter(List<string?> list, NetworkPacketWriter writer)
    {
        writer.WriteInteger(list.Count);
            
        foreach (var item in list)
        {
            writer.WriteString(item ?? "");
        }
    }

    private static void WriteArbitraryListPropertyToWriter(PropertyInfo propertyInfo, NetworkPacketWriter writer, object packet)
    {
        var elements = (ICollection)propertyInfo.GetValue(packet)!;
        writer.WriteInteger(elements.Count);

        foreach (var element in elements)
        {
            var properties = element.GetType().GetProperties();
            
            foreach (var elementProperty in properties)
            {
                WriteProperty(elementProperty, writer, element);
            }
        }
    }

    private static void WriteProperty(PropertyInfo property, NetworkPacketWriter writer, object packet)
    {
        if (TryWritePrimitive(property, packet, writer))
        {
            return;
        }

        var type = property.PropertyType;
        var value = property.GetValue(packet)!;

        if (type == typeof(List<string>))
        {
            WriteStringListPropertyToWriter((List<string?>)value, writer);
            return;
        }

        if (type == typeof(Dictionary<int, string>))
        {
            WriteDictionary((Dictionary<int, string?>)value, writer, writer.WriteInteger, v => writer.WriteString(v ?? ""));
            return;
        }
        
        if (type == typeof(Dictionary<long, string>))
        {
            WriteDictionary((Dictionary<long, string?>)value, writer, writer.WriteLong, v => writer.WriteString(v ?? ""));
            return;
        }

        if (type == typeof(Dictionary<int, long>))
        {
            WriteDictionary((Dictionary<int, long>)value, writer, writer.WriteInteger, writer.WriteLong);
            return;
        }

        if (type == typeof(Dictionary<int, List<string>>))
        {
            var dict = (Dictionary<int, List<string>>)value;
            writer.WriteInteger(dict.Count);

            foreach (var (key, list) in dict)
            {
                writer.WriteInteger(key);
                foreach (var s in list)
                {
                    writer.WriteString(s);
                }
            }

            return;
        }

        if (type == typeof(Dictionary<string, int>))
        {
            WriteDictionary((Dictionary<string, int>)value, writer, writer.WriteString, writer.WriteInteger);
            return;
        }

        if (type == typeof(Dictionary<string, string>))
        {
            WriteDictionary((Dictionary<string, string>)value, writer, writer.WriteString, writer.WriteString);
            return;
        }

        if (type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(List<>) ||
             type.GetGenericTypeDefinition() == typeof(Collection<>)))
        {
            WriteArbitraryListPropertyToWriter(property, writer, packet);
            return;
        }

        if (type != typeof(Dictionary<PropertyInfo, Action<NetworkPacketWriter>>) &&
            type != typeof(Dictionary<PropertyInfo, KeyValuePair<Type, Func<object, object>>>))
        {
            AddObjectToWriter(value, writer, true);
            return;
        }
        
        WriteType(type, value, writer);
    }

    private static void WriteType(Type type, object value, NetworkPacketWriter writer)
    {
        if (primitiveWriters.TryGetValue(type, out var writerAction))
        {
            writerAction(value, writer);
        }
    }
}
