using Inventory.Application;using Xunit;
namespace Inventory.Tests;
public class ValidationTests {
 [Fact] public async Task Product_requires_name_and_sku(){var result=await new CreateProductValidator().ValidateAsync(new CreateProduct("","","General",5,2,1,"Main"));Assert.False(result.IsValid);Assert.Contains(result.Errors,e=>e.PropertyName=="Name");Assert.Contains(result.Errors,e=>e.PropertyName=="Sku");}
 [Fact] public async Task Product_rejects_negative_values(){var result=await new CreateProductValidator().ValidateAsync(new CreateProduct("Mouse","M-01","Office",-1,-3,-2,"Main"));Assert.False(result.IsValid);Assert.Equal(3,result.Errors.Count);}
 [Fact] public async Task Product_accepts_valid_input(){var result=await new CreateProductValidator().ValidateAsync(new CreateProduct("Mouse","M-01","Office",29.99m,14,5,"Main"));Assert.True(result.IsValid);}
 [Fact] public void Order_requires_at_least_one_line(){var error=Assert.Throws<InvalidOperationException>(()=>OrderRules.Validate([]));Assert.Contains("order",error.Message);}
 [Fact] public void Order_rejects_zero_or_negative_quantities(){Assert.Throws<InvalidOperationException>(()=>OrderRules.Validate([new Inventory.Domain.OrderLine{ProductId="p1",Quantity=0}]));Assert.Throws<InvalidOperationException>(()=>OrderRules.Validate([new Inventory.Domain.OrderLine{ProductId="p1",Quantity=-2}]));}
 [Fact] public void Order_total_uses_each_line_price_and_quantity(){var total=OrderRules.CalculateTotal([new Inventory.Domain.OrderLine{ProductId="p1",Price=12.5m,Quantity=2},new Inventory.Domain.OrderLine{ProductId="p2",Price=4m,Quantity=3}]);Assert.Equal(37m,total);}
}
