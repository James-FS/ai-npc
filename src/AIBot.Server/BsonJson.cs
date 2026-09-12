using System;
using MongoDB.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBot.Server
{
    /// <summary>
    /// BsonValue → JToken 的直接映射。**禁止**用 BsonValue.ToJson() 后再 JToken.Parse 回读：
    /// 默认 Shell 模式会把 Int64 写成 NumberLong(...)、DateTime 写成 ISODate(...)——不是合法 JSON，
    /// Newtonsoft 会抛 JsonReaderException（实测 NumberLong 被误读为 NaN）。
    /// </summary>
    public static class BsonJson
    {
        public static JToken ToJToken(BsonValue value)
        {
            if (value == null || value.IsBsonNull) return JValue.CreateNull();
            switch (value.BsonType)
            {
                case BsonType.Document:
                    var obj = new JObject();
                    foreach (BsonElement element in value.AsBsonDocument)
                        obj[element.Name] = ToJToken(element.Value);
                    return obj;
                case BsonType.Array:
                    var array = new JArray();
                    foreach (BsonValue item in value.AsBsonArray)
                        array.Add(ToJToken(item));
                    return array;
                case BsonType.String:
                    return new JValue(value.AsString);
                case BsonType.Int32:
                case BsonType.Int64:
                    return new JValue(value.ToInt64());
                case BsonType.Double:
                    return new JValue(value.AsDouble);
                case BsonType.Boolean:
                    return new JValue(value.AsBoolean);
                case BsonType.DateTime:
                    return new JValue(value.ToUniversalTime().ToString("o"));
                case BsonType.ObjectId:
                    return new JValue(value.AsObjectId.ToString());
                case BsonType.Binary:
                    return new JValue(Convert.ToBase64String(value.AsBsonBinaryData.Bytes));
                case BsonType.Decimal128:
                    return new JValue(value.AsDecimal);
                case BsonType.Timestamp:
                    return new JValue(value.AsBsonTimestamp.Value);
                default:
                    return new JValue(value.ToString());
            }
        }

        /// <summary>把 BsonValue 还原成 Newtonsoft 能反序列化的普通 JSON 字符串。</summary>
        public static string ToJsonString(BsonValue value)
        {
            return ToJToken(value).ToString(Formatting.None);
        }
    }
}
