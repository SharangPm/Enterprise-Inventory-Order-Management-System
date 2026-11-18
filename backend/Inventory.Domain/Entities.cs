using MongoDB.Bson;using MongoDB.Bson.Serialization.Attributes;
namespace Inventory.Domain;
public abstract class Document { [BsonId][BsonRepresentation(BsonType.ObjectId)] public string? Id {get;set;} public DateTime CreatedAt {get;set;}=DateTime.UtcNow; }
public sealed class Product:Document { public string Name {get;set;}="";public string Sku {get;set;}="";public string Barcode {get;set;}="";public string Category {get;set;}="General";public decimal Price {get;set;}public int Stock {get;set;}public int ReorderLevel {get;set;}=5;public string Warehouse {get;set;}="Main warehouse"; }
public sealed class Category:Document { public string Name {get;set;}="";public string Description {get;set;}="";public string Color {get;set;}="#3974ed"; }
public sealed class Warehouse:Document {public string Name {get;set;}="";public string Location {get;set;}="";public int Capacity {get;set;} }
public sealed class InventoryTransaction:Document {public string ProductId {get;set;}="";public string Sku {get;set;}="";public string Type {get;set;}="";public int Quantity {get;set;}public string Reference {get;set;}="";public string User {get;set;}="system";}
public sealed class Order:Document {public string Customer {get;set;}="";public List<OrderLine> Items {get;set;}=[];public decimal Total {get;set;}public string Status {get;set;}="Pending";public string Number {get;set;}="";}
public sealed class OrderLine {public string ProductId {get;set;}="";public string Name {get;set;}="";public int Quantity {get;set;}public decimal Price {get;set;}}
public sealed class PurchaseOrder:Document {public string Supplier {get;set;}="";public List<OrderLine> Items {get;set;}=[];public decimal Total {get;set;}public string Status {get;set;}="Pending";public string Number {get;set;}="";}
public sealed class Contact:Document {public string Name {get;set;}="";public string Email {get;set;}="";public string Phone {get;set;}="";public string Company {get;set;}="";public string Status {get;set;}="Active";}
public sealed class AppUser:Document {public string Email {get;set;}="";public string PasswordHash {get;set;}="";public string Name {get;set;}="";public string Role {get;set;}="Staff";}
public sealed class Notification:Document {public string Title {get;set;}="";public string Message {get;set;}="";public string Type {get;set;}="info";public bool Read {get;set;}}
