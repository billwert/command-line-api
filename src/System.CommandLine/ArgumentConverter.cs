﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine.Binding;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using static System.CommandLine.ArgumentResult;

namespace System.CommandLine
{
    internal static class ArgumentConverter
    {
        private static readonly ConcurrentDictionary<Type, ConvertString> _stringConverters = new ConcurrentDictionary<Type, ConvertString>();

        internal static ArgumentResult Parse(Type type, object value)
        {
            switch (value)
            {
                // try to parse the single string argument to the requested type
                case string argument:
                    return Parse(type, argument);

                // try to parse the multiple string arguments to the request type
                case IReadOnlyCollection<string> arguments:
                    return ParseMany(type, arguments);

                case null:
                    if (type == typeof(bool))
                    {
                        // the presence of the parsed symbol is treated as true
                        return new SuccessfulArgumentResult(true);
                    }

                    break;
            }

            return null;
        }

        public static ArgumentResult Parse(Type type, string value)
        {
            if (_stringConverters.TryGetValue(type, out var convert))
            {
                return convert(value);
            }

            if (TypeDescriptor.GetConverter(type) is TypeConverter typeConverter)
            {
                if (typeConverter.CanConvertFrom(typeof(string)))
                {
                    try
                    {
                        return Success(typeConverter.ConvertFromInvariantString(value));
                    }
                    catch (Exception)
                    {
                        return Failure(type, value);
                    }
                }
            }

            if (type.TryFindConstructorWithSingleParameterOfType(
                typeof(string), out var x))
            {
                convert = _stringConverters.GetOrAdd(
                    type,
                    _ => arg =>
                    {
                        if (arg == null && 
                            !x.parameterDescriptor.AllowsNull)
                        {
                            return Success(type.GetDefaultValueForType());
                        }

                        var instance = x.ctor.Invoke(new object[]
                        {
                            arg
                        });
                        return Success(instance);
                    });

                return convert(value);
            }

            return Failure(type, value);
        }

        public static ArgumentResult ParseMany(
            Type type, 
            IReadOnlyCollection<string> arguments)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            Type itemType;

            if (type == typeof(string))
            {
                // don't treat items as char
                itemType = typeof(string);
            }
            else
            {
                itemType = GetItemTypeIfEnumerable(type);
            }

            var allParseResults = arguments
                                  .Select(arg => Parse(itemType, arg))
                                  .ToArray();

            var successfulParseResults = allParseResults
                                         .OfType<SuccessfulArgumentResult>()
                                         .ToArray();

            if (successfulParseResults.Length == arguments.Count)
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));

                foreach (var parseResult in successfulParseResults)
                {
                    list.Add(parseResult.Value);
                }

                var value = type.IsArray
                                ? (object)Enumerable.ToArray((dynamic)list)
                                : list;

                return Success(value);
            }
            else
            {
                return allParseResults.OfType<FailedArgumentResult>().First();
            }
        }

        private static Type GetItemTypeIfEnumerable(Type type)
        {
            var enumerableInterface =
                IsEnumerable(type)
                    ? type
                    : type
                      .GetInterfaces()
                      .FirstOrDefault(IsEnumerable);

            if (enumerableInterface == null)
            {
                return null;
            }

            return enumerableInterface.GenericTypeArguments[0];

            bool IsEnumerable(Type i)
            {
                return i.IsGenericType &&
                       i.GetGenericTypeDefinition() == typeof(IEnumerable<>);
            }
        }

        private static FailedArgumentResult Failure(Type type, string value)
        {
            return new FailedArgumentTypeConversionResult(type, value);
        }

        public static bool CanBeBoundFromScalarValue(this Type type)
        {
            if (type.IsPrimitive ||
                type.IsEnum)
            {
                return true;
            }

            if (type == typeof(string))
            {
                return true;
            }

            if (TypeDescriptor.GetConverter(type) is TypeConverter typeConverter &&
                typeConverter.CanConvertFrom(typeof(string)))
            {
                return true;
            }

            if (TryFindConstructorWithSingleParameterOfType(type, typeof(string), out _) )
            {
                return true;
            }

            if (GetItemTypeIfEnumerable(type) is Type itemType)
            {
                return itemType.CanBeBoundFromScalarValue();
            }

            return false;
        }

        private static bool TryFindConstructorWithSingleParameterOfType(
            this Type type,
            Type parameterType,
            out (ConstructorInfo ctor, ParameterDescriptor parameterDescriptor) info)
        {
            var (x, y) = type.GetConstructors()
                             .Select(c => (ctor: c, parameters: c.GetParameters()))
                             .SingleOrDefault(tuple => tuple.ctor.IsPublic &&
                                                       tuple.parameters.Length == 1 &&
                                                       tuple.parameters[0].ParameterType == parameterType);

            if (x != null)
            {
                info = (x, new ParameterDescriptor(y[0], new ConstructorDescriptor(x, ModelDescriptor.FromType(type))));
                return true;
            }
            else
            {
                info = (null, null);
                return false;
            }
        }
    }
}
