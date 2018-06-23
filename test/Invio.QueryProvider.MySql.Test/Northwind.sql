create table Employee
(
  EmployeeID int auto_increment primary key,
  LastName nvarchar(20) not null,
  FirstName nvarchar(10) not null,
  Title nvarchar(30),
  TitleOfCourtesy nvarchar(25),
  BirthDate datetime(3),
  HireDate datetime(3),
  Address nvarchar(60),
  City nvarchar(15),
  Region nvarchar(15),
  PostalCode nvarchar(10),
  Country nvarchar(15),
  HomePhone nvarchar(24),
  Extension nvarchar(4),
  Photo longblob,
  Notes longtext,
  ReportsTo int,
  PhotoPath nvarchar(255),
  constraint CK_Birthdate check (`BirthDate` < now()),
  constraint FK_Employee_Employee foreign key (ReportsTo) references Employee(EmployeeID)
);

create index Employee_LastName on Employee (LastName);

create index Employee_PostalCode on Employee (PostalCode);


create table Category
(
  CategoryID int auto_increment primary key,
  CategoryName nvarchar(15) not null,
  Description longtext,
  Picture longblob
);

create index Category_CategoryName on Category (CategoryName);


create table Customer
(
  CustomerID nchar(5) not null primary key,
  CompanyName nvarchar(40) not null,
  ContactName nvarchar(30),
  ContactTitle nvarchar(30),
  Address nvarchar(60),
  City nvarchar(15),
  Region nvarchar(15),
  PostalCode nvarchar(10),
  Country nvarchar(15),
  Phone nvarchar(24),
  Fax nvarchar(24)
);

create index Customer_City on Customer (City);

create index Customer_CompanyName on Customer (CompanyName);

create index Customer_PostalCode on Customer (PostalCode);

create index Customer_Region on Customer (Region);


create table Shipper
(
  ShipperID int auto_increment primary key,
  CompanyName nvarchar(40) not null,
  Phone nvarchar(24)
);

create table Supplier
(
  SupplierID int auto_increment primary key,
  CompanyName nvarchar(40) not null,
  ContactName nvarchar(30),
  ContactTitle nvarchar(30),
  Address nvarchar(60),
  City nvarchar(15),
  Region nvarchar(15),
  PostalCode nvarchar(10),
  Country nvarchar(15),
  Phone nvarchar(24),
  Fax nvarchar(24),
  HomePage longtext
);

create index Supplier_CompanyName on Supplier (CompanyName);

create index Supplier_PostalCode on Supplier (PostalCode);


create table `Order`
(
  OrderID int auto_increment primary key,
  CustomerID nchar(5),
  EmployeeID int,
  OrderDate datetime(3),
  RequiredDate datetime(3),
  ShippedDate datetime(3),
  ShipVia int,
  Freight decimal(15,4) default 0,
  ShipName nvarchar(40),
  ShipAddress nvarchar(60),
  ShipCity nvarchar(15),
  ShipRegion nvarchar(15),
  ShipPostalCode nvarchar(10),
  ShipCountry nvarchar(15),

  constraint FK_Order_Customer foreign key (CustomerID) references Customer(CustomerID),
  constraint FK_Order_Employee foreign key (EmployeeID) references Employee(EmployeeID),
  constraint FK_Order_Shipper foreign key  (ShipVia) references Shipper(ShipperID)
);

create index Order_CustomerID on `Order` (CustomerID);

create index Order_EmployeeID on `Order` (EmployeeID);

create index Order_OrderDate on `Order` (OrderDate);

create index Order_ShippedDate on `Order` (ShippedDate);

create index Order_ShipVia on `Order` (ShipVia);

create index Order_ShipPostalCode on `Order` (ShipPostalCode);


create table Product
(
  ProductID int auto_increment primary key,
  ProductName nvarchar(40) not null,
  SupplierID int,
  CategoryID int,
  QuantityPerUnit nvarchar(20),
  UnitPrice decimal(15,4) default 0,
  UnitsInStock smallint default 0,
  UnitsOnOrder smallint default 0,
  ReorderLevel smallint default 0,
  Discontinued tinyint default 0 not null,

  constraint FK_Product_Supplier foreign key (SupplierID) references Supplier(SupplierID),
  constraint FK_Product_Category foreign key (CategoryID) references Category(CategoryID),
  constraint CK_Product_UnitPrice check (`UnitPrice` >= 0),
  constraint CK_UnitsInStock check (`UnitsInStock` >= 0),
  constraint CK_UnitsOnOrder check (`UnitsOnOrder` >= 0),
  constraint CK_ReorderLevel check (`ReorderLevel` >= 0)
);

create index Product_CategoryID on Product (CategoryID);

create index Product_ProductName on Product (ProductName);

create index Produt_SupplierID on Product (SupplierID);


create table OrderDetail
(
  OrderID int not null,
  ProductID int not null,
  UnitPrice decimal(15,4) default 0 not null,
  Quantity smallint default 1 not null,
  Discount real default 0 not null,

  constraint FK_OrderDetail_Order foreign key (OrderID) references `Order`(OrderID),
  constraint FK_OrderDetail_Product foreign key (ProductID) references Product(ProductID),
  constraint CK_UnitPrice check (`UnitPrice` >= 0),
  constraint CK_Quantity check (`Quantity` > 0),
  constraint CK_Discount check (`Discount` >= 0 AND `Discount` <= 1),
  constraint PK_OrderDetail primary key (OrderID, ProductID)
);

create index OrderDetail_OrderID on OrderDetail (OrderID);

create index OrderDetail_ProductID on OrderDetail (ProductID);


create table Region
(
  RegionID int not null primary key,
  RegionDescription nchar(50) not null
);

create table Territory
(
  TerritoryID nvarchar(20) not null primary key,
  TerritoryDescription nchar(50) not null,
  RegionID int not null,

  constraint FK_Territory_Region foreign key (RegionID) references Region(RegionID)
);

create table EmployeeTerritory
(
  EmployeeID int not null,
  TerritoryID nvarchar(20) not null,

  constraint PK_EmployeeTerritory primary key (EmployeeID, TerritoryID),
  constraint FK_EmployeeTerritory_Employee foreign key (EmployeeID) references Employee(EmployeeID),
  constraint FK_EmployeeTerritory_Territory foreign key (TerritoryID) references Territory(TerritoryID)
);

create table TestModel
(
  Id varchar(36) not null primary key,
  Name nchar(50) null,
  InStock bit default 0 not null,
  ExpirationDate datetime(3) not null,
  LotSize int default 0 not null,
  Price decimal(15, 4) default 0 not null
);