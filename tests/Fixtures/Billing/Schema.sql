CREATE TABLE [dbo].[Customers] (
    [CustomerId] bigint NOT NULL,
    [Reference] varchar(50) NULL,
    CONSTRAINT [PK_Billing_Customers] PRIMARY KEY ([CustomerId])
);
GO
CREATE TABLE [dbo].[Invoices] (
    [InvoiceId] bigint NOT NULL,
    [CustomerId] bigint NOT NULL,
    CONSTRAINT [PK_Invoices] PRIMARY KEY ([InvoiceId]),
    CONSTRAINT [FK_Invoices_Customers] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customers] ([CustomerId])
);
GO
