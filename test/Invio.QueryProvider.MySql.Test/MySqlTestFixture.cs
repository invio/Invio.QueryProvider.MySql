using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Invio.Extensions;
using Invio.QueryProvider.Test;
using Invio.QueryProvider.Test.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.FSharp.Core;
using MySql.Data.MySqlClient;

namespace Invio.QueryProvider.MySql.Test {
    public sealed class MySqlTestFixture : IDisposable {
        private static IImmutableDictionary<Type, MySqlDbType> customDbTypeMappings { get; } =
            ImmutableDictionary<Type, MySqlDbType>.Empty
                .Add(typeof(PhoneNumber), MySqlDbType.VarChar);

        private static IImmutableDictionary<Type, Type> customTypeMappings { get; } =
            ImmutableDictionary<Type, Type>.Empty
                .Add(typeof(PhoneNumber), typeof(String));

        private static MySqlConnectionSettings configuration { get; }

        private Guid testId { get; } = Guid.NewGuid();
        private String databaseName => $"Northwind_{this.testId}";

        static MySqlTestFixture() {
            var basePath = Environment.GetEnvironmentVariable("BASE_DIRECTORY");

            if (basePath == null) {
                basePath = AppContext.BaseDirectory;

                // cross-platform equivalent of "../../../../"
                for (var index = 0; index < 4; index++) {
                    basePath = Directory.GetParent(basePath).FullName;
                }
            }

            configuration = new MySqlConnectionSettings();
            var path = Path.Combine("config", "appsettings.json");

            Console.Error.WriteLine("BasePath: " + basePath);
            new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(path, optional: true)
                .AddEnvironmentVariables()
                .Build()
                .GetSection("MySql")
                .Bind(configuration);
        }

        public MySqlTestFixture() {
            // Create DB
            using (var connection = this.OpenConnection(null)) {
                CreateDatabase(connection, this.databaseName);
            }

            // Create Tables
            String commandText;
            using (var stream = OpenNorthwindSqlResource())
            using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                commandText = reader.ReadToEnd();
            }

            using (var connection = this.OpenConnection())
            using (var command = new MySqlCommand(commandText, connection)) {
                command.ExecuteNonQuery();
            }

            var testData = new Data();

            // Load Data
            using (var connection = this.OpenConnection())
            using (var tx = connection.BeginTransaction()) {
                this.InsertData(connection, testData.Categories);
                this.InsertData(connection, testData.Regions);
                this.InsertData(connection, testData.Territories);
                this.InsertData(connection, testData.Suppliers);
                this.InsertData(connection, testData.Products);
                this.InsertData(connection, testData.Shippers);
                this.InsertData(connection, testData.Customers);
                this.InsertData(connection, testData.Employees);
                this.InsertData(connection, testData.EmployeeTerritories);
                this.InsertData(connection, testData.Orders);
                this.InsertData(connection, testData.OrderDetails);

                tx.Commit();
            }
        }

        private void InsertData<T>(MySqlConnection connection, List<T> models) {
            using (var command = new MySqlCommand()) {
                command.Connection = connection;
                var parameters =
                    typeof(T).GetProperties()
                        .Select(p => new {
                            Property = p,
                            Parameter =
                                command.Parameters.Add(
                                    $"@{LowerCaseFirstLetter(p.Name)}",
                                    GetDbType(p.PropertyType)
                                )
                        })
                        .ToList();
                command.CommandText =
                    $"insert into `{typeof(T).Name}` " +
                    $"({String.Join(", ", parameters.Select(p => p.Property.Name.Quote('`', '`')))}) " +
                    $"values ({String.Join(", ", parameters.Select(p => p.Parameter.ParameterName))})";
                command.Prepare();

                foreach (var model in models) {
                    foreach (var param in parameters) {
                        if (customTypeMappings.TryGetValue(param.Property.PropertyType, out var destinationType)) {
                            var typeConverter =
                                TypeDescriptor.GetConverter(param.Property.PropertyType);
                            param.Parameter.Value =
                                typeConverter.ConvertTo(
                                    param.Property.GetValue(model),
                                    destinationType
                                );
                        } else {
                            param.Parameter.Value = param.Property.GetValue(model);
                        }
                    }

                    try {
                        var result = command.ExecuteNonQuery();
                        if (result != 1) {
                            Console.Error.WriteLine(
                                $"Expected 1 row to be modified, but {result} was returned instead."
                            );
                        }
                    } catch (MySqlException ex) {
                        throw new ArgumentException(
                            $"An error occurred inserting the model: {typeof(T).Name} {{" +
                            String.Join(
                                ", ",
                                parameters.Select(p =>
                                    p.Property.Name + " = " + (p.Property.GetValue(model) ?? "null").ToString())) +
                            $"}}\r\n{ex.Message}",
                            nameof(models),
                            ex
                        );
                    }
                }
            }
        }

        private MySqlDbType GetDbType(Type propertyType) {
            var (dataType, _) =
                QueryTranslator.defaultGetMySqlDBType(
                    QueryTranslatorUtilities.TypeSource.NewType(propertyType)
                );

            if (dataType.IsDataType) {
                return ((QueryTranslatorUtilities.DBType<MySqlDbType>.DataType)dataType).Item;
            } else if (customDbTypeMappings.TryGetValue(propertyType, out var dbType)) {
                return dbType;
            } else {
                throw new ArgumentException(
                    $"The specified Type is not supported: {propertyType.Name}",
                    nameof(propertyType)
                );
            }
        }

        private static Stream OpenNorthwindSqlResource() {
            return typeof(MySqlTestFixture)
                .Assembly
                .GetManifestResourceStream("Invio.QueryProvider.MySql.Test.Northwind.sql");
        }

        public MySqlConnection OpenConnection() {
            return this.OpenConnection(this.databaseName);
        }

        private MySqlConnection OpenConnection(String defaultDatabase) {
            var connection = new MySqlConnection(GetConnectionString(defaultDatabase));

            connection.Open();

            return connection;
        }

        public IQueryable<TModel> CreateQueryable<TModel>(MySqlConnection connection) {
            var queryProvider = new MySqlQueryProvider(connection, customTypeMappings);
            return new Query<TModel>(queryProvider, FSharpOption<Expression>.None);
        }

        public void Dispose() {
            // Drop DB
            using (var connection = this.OpenConnection(null)) {
                DropDatabase(connection, this.databaseName);
            }
        }

        private static String GetConnectionString(String defaultDatabase) {
            var builder = new MySqlConnectionStringBuilder {
                UserID = configuration.User,
                Password = configuration.Password,
                Port = configuration.Port,
                Server = configuration.Host,
                SslMode = configuration.SslMode
            };

            if (defaultDatabase != null) {
                builder.Database = defaultDatabase;
            }

            return builder.GetConnectionString(includePass: true);
        }

        private static void CreateDatabase(MySqlConnection connection, String databaseName) {
            var commandText =
                $"CREATE DATABASE `{databaseName}` " +
                $"DEFAULT CHARACTER SET utf8 " +
                $"DEFAULT COLLATE utf8_general_ci";

            using (var command = new MySqlCommand(commandText, connection)) {
                command.ExecuteNonQuery();
            }
        }

        private static void DropDatabase(MySqlConnection connection, String databaseName) {
            var commandText = $"DROP DATABASE `{databaseName}`";

            using (var command = new MySqlCommand(commandText, connection)) {
                command.ExecuteNonQuery();
            }
        }

        private static String LowerCaseFirstLetter(String str) {
            return $"{Char.ToLower(str[0])}{str.Substring(1)}";
        }
    }
}
