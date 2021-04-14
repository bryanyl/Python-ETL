﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Smartstore.Core.Catalog.Products;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Checkout.Tax;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;
using Smartstore.Core.Messages;
using Smartstore.Core.Security;
using Smartstore.Core.Seo;
using Smartstore.Web.Models.Orders;

namespace Smartstore.Web.Controllers
{
    public class ReturnRequestController : PublicControllerBase
    {
        private readonly SmartDbContext _db;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly ICurrencyService _currencyService;
        private readonly ProductUrlHelper _productUrlHelper;
        private readonly IMessageFactory _messageFactory;
        private readonly OrderSettings _orderSettings;
        private readonly LocalizationSettings _localizationSettings;

        public ReturnRequestController(
            SmartDbContext db, 
            IOrderProcessingService orderProcessingService,
            ICurrencyService currencyService,
            ProductUrlHelper productUrlHelper,
            IMessageFactory messageFactory,
            OrderSettings orderSettings,
            LocalizationSettings localizationSettings)
        {
            _db = db;
            _orderProcessingService = orderProcessingService;
            _currencyService = currencyService;
            _productUrlHelper = productUrlHelper;
            _messageFactory = messageFactory;
            _orderSettings = orderSettings;
            _localizationSettings = localizationSettings;
        }

        /// <param name="id"><see cref="Order.Id"/></param>
        [RequireSsl]
        public async Task<IActionResult> ReturnRequest(int id)
        {
            var order = await _db.Orders.FindByIdAsync(id, false);

            if (order == null || Services.WorkContext.CurrentCustomer.Id != order.CustomerId)
            {
                return new UnauthorizedResult();
            }
            
            if (!_orderProcessingService.IsReturnRequestAllowed(order))
            {
                return RedirectToRoute("Homepage");
            }
            
            var model = new SubmitReturnRequestModel();
            model = await PrepareReturnRequestModelAsync(model, order);
            return View(model);
        }

        /// <param name="id"><see cref="Order.Id"/></param>
        [HttpPost, ActionName("ReturnRequest")]
        public async Task<IActionResult> ReturnRequestSubmit(int id, SubmitReturnRequestModel model)
        {
            var order = await _db.Orders
                .Include(x => x.BillingAddress)
                .Include(x => x.ShippingAddress)
                .Include(x => x.OrderItems)
                .ThenInclude(x => x.Product)
                .FindByIdAsync(id, false);

            // TODO: (mh) (core) Will fail on sending message because Customer entity is included.
            //var order = await _db.Orders
            //    .FindByIdAsync(id);

            var customer = Services.WorkContext.CurrentCustomer;
            
            if (order == null || customer.Id != order.CustomerId)
            {
                return new UnauthorizedResult();
            }

            if (!_orderProcessingService.IsReturnRequestAllowed(order))
            {
                return RedirectToRoute("Homepage");
            }

            foreach (var orderItem in order.OrderItems)
            {
                var form = Request.Form;

                var quantity = 0;
                foreach (var formKey in form.Keys)
                {
                    if (formKey.EqualsNoCase($"quantity{orderItem.Id}"))
                    {
                        _ = int.TryParse(form[formKey], out quantity);
                        break;
                    }
                }

                if (quantity > 0)
                {
                    var rr = new ReturnRequest
                    {
                        StoreId = Services.StoreContext.CurrentStore.Id,
                        OrderItemId = orderItem.Id,
                        Quantity = quantity,
                        CustomerId = customer.Id,
                        ReasonForReturn = model.ReturnReason,
                        RequestedAction = model.ReturnAction,
                        CustomerComments = model.Comments,
                        StaffNotes = string.Empty,
                        ReturnRequestStatus = ReturnRequestStatus.Pending
                    };

                    customer.ReturnRequests.Add(rr);

                    _db.TryUpdate(customer);
                    await _db.SaveChangesAsync();

                    model.AddedReturnRequestIds.Add(rr.Id);

                    // Notify store owner here by sending an email.
                    await _messageFactory.SendNewReturnRequestStoreOwnerNotificationAsync(rr, orderItem, _localizationSettings.DefaultAdminLanguageId);
                }
            }

            model = await PrepareReturnRequestModelAsync(model, order);

            if (model.AddedReturnRequestIds.Any())
            {
                model.Result = T("ReturnRequests.Submitted");
            }
            else
            {
                NotifyWarning(T("ReturnRequests.NoItemsSubmitted"));
            }

            return View(model);
        }

        [NonAction]
        protected async Task<SubmitReturnRequestModel> PrepareReturnRequestModelAsync(SubmitReturnRequestModel model, Order order)
        {
            Guard.NotNull(order, nameof(order));
            Guard.NotNull(model, nameof(model));

            model.OrderId = order.Id;

            var language = Services.WorkContext.WorkingLanguage;
            string returnRequestReasons = _orderSettings.GetLocalizedSetting(x => x.ReturnRequestReasons, order.CustomerLanguageId, order.StoreId, true, false);
            string returnRequestActions = _orderSettings.GetLocalizedSetting(x => x.ReturnRequestActions, order.CustomerLanguageId, order.StoreId, true, false);

            // Return reasons.
            var availableReturnReasons = new List<SelectListItem>();
            foreach (var rrr in returnRequestReasons.SplitSafe(","))
            {
                availableReturnReasons.Add(new SelectListItem { Text = rrr, Value = rrr });
            }
            ViewBag.AvailableReturnReasons = availableReturnReasons;

            // Return actions.
            var availableReturnActions = new List<SelectListItem>();
            foreach (var rra in returnRequestActions.SplitSafe(","))
            {
                availableReturnActions.Add(new SelectListItem { Text = rra, Value = rra });
            }
            ViewBag.AvailableReturnActions = availableReturnActions;

            // Products.
            var orderItems = await _db.OrderItems
                .Include(x => x.Product)
                .ApplyStandardFilter(order.Id)
                .ToListAsync();
            
            foreach (var orderItem in orderItems)
            {
                var orderItemModel = new SubmitReturnRequestModel.OrderItemModel
                {
                    Id = orderItem.Id,
                    ProductId = orderItem.Product.Id,
                    ProductName = orderItem.Product.GetLocalized(x => x.Name),
                    ProductSeName = await orderItem.Product.GetActiveSlugAsync(),
                    AttributeInfo = orderItem.AttributeDescription,
                    Quantity = orderItem.Quantity
                };

                orderItemModel.ProductUrl = await _productUrlHelper.GetProductUrlAsync(orderItemModel.ProductSeName, orderItem);

                // TODO: (mh) (core) Reconsider when pricing is available.
                var customerCurrency = await _db
                    .Currencies
                    .Where(x => x.CurrencyCode == order.CustomerCurrencyCode)
                    .FirstOrDefaultAsync();

                // Unit price.
                switch (order.CustomerTaxDisplayType)
                {
                    case TaxDisplayType.ExcludingTax:
                        {
                            // TODO: (mh) (core) _currencyService.ConvertToCurrency doesn't take a rate as paramter. 
                            //var unitPriceExclTaxInCustomerCurrency = _currencyService.ConvertCurrency(orderItem.UnitPriceExclTax, order.CurrencyRate);
                            //orderItemModel.UnitPrice = _priceFormatter.FormatPrice(unitPriceExclTaxInCustomerCurrency, true, order.CustomerCurrencyCode, language, false);
                            orderItemModel.UnitPrice = new Money(orderItem.UnitPriceExclTax, customerCurrency);
                        }
                        break;
                    case TaxDisplayType.IncludingTax:
                        {
                            //var unitPriceInclTaxInCustomerCurrency = _currencyService.ConvertCurrency(orderItem.UnitPriceInclTax, order.CurrencyRate);
                            //orderItemModel.UnitPrice = _priceFormatter.FormatPrice(unitPriceInclTaxInCustomerCurrency, true, order.CustomerCurrencyCode, language, true);
                            orderItemModel.UnitPrice = new Money(orderItem.UnitPriceInclTax, customerCurrency);
                        }
                        break;
                }

                model.Items.Add(orderItemModel);
            }

            return model;
        }
    }
}