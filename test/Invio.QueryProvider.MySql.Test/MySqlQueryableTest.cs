using System;
using System.Linq;
using Invio.QueryProvider.Test.CSharp;
using Invio.QueryProvider.Test.Models;
using MySql.Data.MySqlClient;
using Xunit;

namespace Invio.QueryProvider.MySql.Test {
    public sealed class MySqlQueryableTest :
        QueryableTestBase, IClassFixture<MySqlTestFixture>, IDisposable {

        private MySqlConnection connection { get; }

        private Lazy<IQueryable<Product>> products { get; }
        private Lazy<IQueryable<Customer>> customers { get; }
        private Lazy<IQueryable<Employee>> employees { get; }
        private Lazy<IQueryable<Order>> orders { get; }
        private Lazy<IQueryable<Shipper>> shippers { get; }
        private Lazy<IQueryable<Supplier>> suppliers { get; }
        private Lazy<IQueryable<Category>> categories { get; }

        protected override IQueryable<Product> Products => this.products.Value;
        protected override IQueryable<Customer> Customers => this.customers.Value;
        protected override IQueryable<Employee> Employees => this.employees.Value;
        protected override IQueryable<Order> Orders => this.orders.Value;
        protected override IQueryable<Shipper> Shippers => this.shippers.Value;
        protected override IQueryable<Supplier> Suppliers => this.suppliers.Value;
        protected override IQueryable<Category> Categories => this.categories.Value;

        public MySqlQueryableTest(MySqlTestFixture fixture) {
            this.connection = fixture.OpenConnection();

            this.products =
                new Lazy<IQueryable<Product>>(
                    () => fixture.CreateQueryable<Product>(this.connection)
                );
            this.customers =
                new Lazy<IQueryable<Customer>>(
                    () => fixture.CreateQueryable<Customer>(this.connection)
                );
            this.employees =
                new Lazy<IQueryable<Employee>>(
                    () => fixture.CreateQueryable<Employee>(this.connection)
                );
            this.orders =
                new Lazy<IQueryable<Order>>(
                    () => fixture.CreateQueryable<Order>(this.connection)
                );
            this.shippers =
                new Lazy<IQueryable<Shipper>>(
                    () => fixture.CreateQueryable<Shipper>(this.connection)
                );
            this.suppliers =
                new Lazy<IQueryable<Supplier>>(
                    () => fixture.CreateQueryable<Supplier>(this.connection)
                );
            this.categories =
                new Lazy<IQueryable<Category>>(
                    () => fixture.CreateQueryable<Category>(this.connection)
                );
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void All_ConditionFalse() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void All_ConditionTrue() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Count_Distinct() {
        }

        [Fact]
        public override void Count_Filtered() {
            // TODO: Implement support for Count w/ predicate
            var result = this.Suppliers.Where(s => s.Country == "USA").Count();

            Assert.Equal(4, result);
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Distinct_Anonymous() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Distinct_Scalar() {
        }

        [Fact(Skip = "BUG: FirstOrDefault generates NullReferenceException when there are no results.")]
        public override void FirstOrDefault_NoResults() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Join_InnerJoin() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Join_LeftJoin() {
        }

        [Fact(Skip = "BUG: Expression in OrderBy results in invalid SQL string.")]
        public override void OrderBy_Expression() {
            base.OrderBy_Expression();
        }

        [Fact(Skip = "TODO: Not Supported (Skip must be combined with Take)")]
        public override void Skip() {
        }

        [Fact(Skip = "TODO: Not Supported (Multiple Skips not supported)")]
        public override void Skip_Skip() {
        }

        [Fact(Skip = "TODO: Not Supported (Multiple Takes not supported)")]
        public override void Take_Take() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Select_AnonymousType_Complex() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Select_AnonymousType_Composite() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Select_AnonymousType_Simple() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Select_Arithmetic() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Select_StringLength() {
        }

        [Fact(Skip = "BUG: explict casts are dopped, therefore lossy, numeric conversions do not work")]
        public override void Where_Field_Eq_ExplicitConversion() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Where_LiteralArray_Contains() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Where_String_Length() {
        }

        [Fact(Skip = "TODO: Not Supported")]
        public override void Where_String_IndexOf() {
            base.Where_String_IndexOf();
        }

        [Fact(Skip = "BUG: String Contains results in invalid SQL.")]
        public override void Where_String_Contains() {
        }

        [Fact(Skip = "BUG: String StartsWith results in invalid SQL.")]
        public override void Where_String_StartsWith() {
        }

        [Fact(Skip = "BUG: String EndsWith results in invalid SQL.")]
        public override void Where_String_EndsWith() {
        }

        public void Dispose() {
            this.connection.Dispose();
        }
    }
}
