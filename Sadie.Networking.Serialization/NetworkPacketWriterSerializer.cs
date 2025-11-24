using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using Sadie.API;
using Sadie.Core.Shared.Attributes;

namespace Sadie.Networking.Serialization;

public static class NetworkPacketWriterSerializer
{
    private static readonly Dictionary<Type, Action<object, NetworkPacketWriter>> PrimitiveWriters =
        new()
        {
            { typeof(string), (v, w) => w.WriteString((string)v) },
            { typeof(int),    (v, w) => w.WriteInteger((int)v) },
            { typeof(short),  (v, w) => w.WriteShort((short)v) },
            { typeof(long),   (v, w) => w.WriteLong((long)v) },
            { typeof(bool),   (v, w) => w.WriteBool((bool)v) }
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

        return identifierAttribute?.Id ?? 
               throw new InvalidOperationException($"Missing packet identifier attribute for packet type {packetObject.GetType()}");
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

    private static bool TryWritePrimitive(Type type, object value, NetworkPacketWriter writer)
    {
        if (!PrimitiveWriters.TryGetValue(type, out var action))
        {
            return false;
        }
        
        action(value, writer);
        return true;
    }

    private static bool TryWriteList(PropertyInfo property, object packet, NetworkPacketWriter writer)
    {
        var type = property.PropertyType;
        
        if (!type.IsGenericType)
        {
            return false;
        }

        var def = type.GetGenericTypeDefinition();
        
        if (def != typeof(List<>) && def != typeof(Collection<>))
        {
            return false;
        }

        var list = (IEnumerable)property.GetValue(packet)!;
        var items = list.Cast<object>().ToList();

        writer.WriteInteger(items.Count);

        foreach (var item in items)
        {
            if (!TryWritePrimitive(item.GetType(), item, writer))
            {
                AddObjectToWriter(item, writer, true);
            }
        }

        return true;
    }

    private static bool TryWriteDictionary(PropertyInfo property, object packet, NetworkPacketWriter writer)
    {
        var type = property.PropertyType;
        
        if (!type.IsGenericType)
        {
            return false;
        }
        
        if (type.GetGenericTypeDefinition() != typeof(Dictionary<,>))
        {
            return false;
        }

        var dict = (IDictionary)property.GetValue(packet)!;
        writer.WriteInteger(dict.Count);

        foreach (DictionaryEntry entry in dict)
        {
            var kt = entry.Key.GetType();
            var vt = entry.Value?.GetType();

            if (!TryWritePrimitive(kt, entry.Key, writer))
            {
                throw new InvalidOperationException("Unsupported dictionary key type.");
            }

            if (entry.Value == null)
            {
                writer.WriteString("");
                continue;
            }

            if (!TryWritePrimitive(vt!, entry.Value, writer))
            {
                AddObjectToWriter(entry.Value, writer, true);
            }
        }

        return true;
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
            
            if (insteadRuleMap != null && 
                insteadRuleMap.TryGetValue(property, out var value))
            {
                value.Invoke(writer);
                continue;
            }

            if (beforeRuleMap != null && 
                beforeRuleMap.TryGetValue(property, out var beforeValue))
            {
                beforeValue.Invoke(writer);
            }
            
            WriteProperty(property, writer, packet);

            if (afterRuleMap != null && 
                afterRuleMap.TryGetValue(property, out var afterValue))
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

    private static void WriteProperty(PropertyInfo property, NetworkPacketWriter writer, object packet)
    {
        var type = property.PropertyType;
        var value = property.GetValue(packet);

        if (value == null)
        {
            return;
        }

        if (TryWritePrimitive(type, value, writer))
        {
            return;
        }

        if (TryWriteList(property, packet, writer))
        {
            return;
        }

        if (TryWriteDictionary(property, packet, writer))
        {
            return;
        }

        AddObjectToWriter(value, writer, true);
    }

    private static void WriteType(Type type, object value, NetworkPacketWriter writer)
    {
        if (type == typeof(string))
        {
            writer.WriteString((string)value);
        }
        else if (type == typeof(int))
        {
            writer.WriteInteger((int)value);
        }
        else if (type == typeof(long))
        {
            writer.WriteLong((long)value);
        }
        else if (type == typeof(bool))
        {
            writer.WriteBool((bool) value);
        }
        else if (type == typeof(short))
        {
            writer.WriteShort((short) value);
        }
    }
}