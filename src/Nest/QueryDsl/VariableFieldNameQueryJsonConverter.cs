﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Nest.Resolvers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Nest
{
	[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
	internal class VariableFieldAttribute : Attribute
	{
		public string FieldName { get; set; }

		public VariableFieldAttribute() { }

		public VariableFieldAttribute(string fieldName)
		{
			this.FieldName = fieldName;
		}

	}

	internal class VariableFieldNameQueryJsonConverter<TReadAs, TInterface> : ReserializeJsonConverter<TReadAs, IFieldNameQuery>
		where TReadAs : class, IFieldNameQuery, TInterface, new()
	{

		private static readonly ConcurrentDictionary<Type, Tuple<JsonProperty, VariableFieldAttribute>> _cachedTypeInformation =
			new ConcurrentDictionary<Type, Tuple<JsonProperty, VariableFieldAttribute>>();


		protected override object DeserializeJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
		{
			var jo = JObject.Load(reader);
			var query = this.ReadAs(jo.CreateReader(), typeof(TReadAs), existingValue, serializer);

			var info = GetOrCreateTypeInfo(typeof(TInterface));
			var deeperPropertyName = info.Item2.FieldName;

			var knownProperties = typeof (TInterface).GetCachedObjectProperties();
			var unknownProperties = jo.Properties().Select(p => p.Name).Except(knownProperties.Where(p=>p.HasMemberAttribute).Select(p=>p.PropertyName));
			var fieldProperty = unknownProperties.FirstOrDefault();

			query.Field = fieldProperty;

			var locationField = knownProperties.FirstOrDefault(p => p.PropertyName == fieldProperty || p.PropertyName == deeperPropertyName);
			if (locationField == null) return query;

			var propertyValue = jo.Property(fieldProperty).Value;
			var o = JToken.ReadFrom(propertyValue.CreateReader()).Value<JObject>();
			var r = (deeperPropertyName.IsNullOrEmpty() ? (JToken)o : o.Property(deeperPropertyName).Value).CreateReader();

			var locationValue = serializer.Deserialize(r, locationField.PropertyType);
			locationField.ValueProvider.SetValue(query, locationValue);

			return query;
		}

		protected override void SerializeJson(JsonWriter writer, object value, IFieldNameQuery castValue, JsonSerializer serializer)
		{
			var fieldName = castValue.Field;
			if (fieldName == null)
				return;

			var contract = serializer.ContractResolver as SettingsContractResolver;
			if (contract == null)
				return;

			var field = contract.Infer.Field(fieldName);
			if (field.IsNullOrEmpty())
				return;

			var info = GetOrCreateTypeInfo(typeof(TInterface));

			using (var sw = new StringWriter())
			using (var localWriter = new JsonTextWriter(sw))
			{
				this.Reserialize(localWriter, value, serializer);
				var jo = JObject.Parse(sw.ToString());
				var v = info.Item1.ValueProvider.GetValue(castValue);
				JToken o = null;
				if (v != null) o = JToken.FromObject(v);

				if (info.Item2.FieldName.IsNullOrEmpty())
					jo.Add(field, o);
				else
				{
					var subObject = new JObject
					{
						{info.Item2.FieldName, o}
					};
					jo.Add(field, subObject);
				}
				writer.WriteToken(jo.CreateReader());
			}
		}

		private static Tuple<JsonProperty, VariableFieldAttribute> GetOrCreateTypeInfo(Type t)
		{
			Tuple<JsonProperty, VariableFieldAttribute> info;
			if (!_cachedTypeInformation.TryGetValue(t, out info))
			{
				var properties = from prop in t.GetCachedObjectProperties(MemberSerialization.OptOut)
					let attributes = prop.AttributeProvider.GetAttributes(typeof (VariableFieldAttribute), true)
					where attributes.Count > 0
					select Tuple.Create(prop, (VariableFieldAttribute) attributes.First());
				info = properties.FirstOrDefault();
				if (info == null)
					throw new Exception("Can not use VariableFieldNameJsonConverter<T> if T has no property with VariableFieldAttribute set");
				_cachedTypeInformation.TryAdd(t, info);
			}
			return info;
		}
	}
}