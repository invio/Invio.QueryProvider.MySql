using System;
using System.Linq;
using System.Threading.Tasks;
using Invio.Extensions.Linq.Async.Tests;
using MySql.Data.MySqlClient;
using Xunit;

namespace Invio.QueryProvider.MySql.Test {
    public sealed class MySqlQueryableAsyncTest :
        AsyncQueryableTestBase, IClassFixture<MySqlTestFixture>, IDisposable {

        private MySqlConnection connection { get; }

        protected override IQueryable<TestModel> Query { get; }

        public MySqlQueryableAsyncTest(MySqlTestFixture fixture) {
            this.connection = fixture.OpenConnection();
            this.Query = fixture.CreateQueryable<TestModel>(this.connection);
        }

        protected override Task<TResult> VerifyAsyncExecution<TResult>(
            Func<IQueryable<TestModel>, Task<TResult>> test) {

            return test(this.Query);
        }

        public void Dispose() {
            this.connection.Dispose();
        }
    }
}