using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bot46.API.Infrastructure.Extensions;
using Bot46.API.Infrastructure.Models;
using Bot46.API.Infrastructure.Modules;
using Bot46.API.Infrastructure.Services;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Internals.Fibers;
using Microsoft.Bot.Connector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bot46.API.Infrastructure.Dialogs
{
    [Serializable]
    public class CatalogDialog : IDialog<object>
    {
        private readonly ICatalogService serviceCatalog = ServiceResolver.Get<ICatalogService>();
        private readonly IBasketService serviceBasket = ServiceResolver.Get<IBasketService>();
        private static readonly ICatalogAIService serviceCatalogAI = ServiceResolver.Get<ICatalogAIService>();

        private readonly int _itemsPage = 10;
        private int _currentPage = 0;
        internal CatalogFilter _filter = null;

        public JObject ItemToBuy { get; private set; }

        public async Task StartAsync(IDialogContext context)
        {
            if (_filter == null)
            {
                context.Call(new CatalogFilterDialog(), ExecutedCatalogFilterAsync);
            }
            else
            {
                await ShowCatalog(context);
                context.Wait(MessageReceivedAsync);
            }
        }

        private async  Task ExecutedCatalogFilterAsync(IDialogContext context, IAwaitable<CatalogFilter> result)
        {
            _filter = await result;            
            await ShowCatalog(context);
            context.Wait(MessageReceivedAsync);
        }

        private async Task ShowCatalog(IDialogContext context)
        {
            var logged = await context.IsAuthenticated();
            var reply = context.MakeMessage();
            reply.AttachmentLayout = AttachmentLayoutTypes.Carousel;

            Catalog catalog;
            if(_filter.Tags != null)
            {
                catalog = await serviceCatalogAI.GetCatalogItems(_currentPage, _itemsPage, _filter.Brand, _filter.Type, _filter.Tags);
            }
            else
            {
                 catalog = await serviceCatalog.GetCatalogItems(_currentPage, _itemsPage, _filter.Brand, _filter.Type);
            }

            int pageCount = (catalog.Count + _itemsPage - 1) / _itemsPage;
            if (catalog.Count != 0)
            {                 
                reply.Text = $"Page {_currentPage + 1} of {pageCount} ( {catalog.Count} items )";
                reply.Attachments = CatalogCarousel(catalog, logged);
            }
            else
            {
                reply.Text = $"There are no results that match your search.";
            }


            List<CardAction> cardActions = CardActions(pageCount, logged);
            reply.SuggestedActions = new SuggestedActions()
            {
                Actions = cardActions
            };

            await context.PostAsync(reply);
        }

        private List<CardAction> CardActions(int pageCount, bool logged)
        {
            var cardActions = new List<CardAction>();

            cardActions.Add(new CardAction()
            {
                Title = "🏠",
                Type = ActionTypes.PostBack,
                Value = $@"{{ 'ActionType': '{BotActionTypes.Back}'}}"
            });

            if (!logged)
            {
                cardActions.Add(new CardAction()
                {
                    Title = "👤",
                    Type = ActionTypes.PostBack,
                    Value = $@"{{ 'ActionType': '{BotActionTypes.Login}'}}"
                });
            }

            if (_currentPage + 1 < pageCount)
            {
                cardActions.Add(new CardAction()
                {
                    Title = "Show more",
                    Type = ActionTypes.PostBack,
                    Value = $@"{{ 'ActionType': '{BotActionTypes.NextPage}'}}"
                });
            }

            return cardActions;
        }

        private List<Attachment> CatalogCarousel(Catalog catalog, bool logged){
            var attachments = new List<Attachment>();
            foreach(var item in catalog.Data)
            {
                List<CardImage> cardImages = new List<CardImage>();
                cardImages.Add(new CardImage(url:item.PictureUri ));
                List<CardAction> cardButtons = new List<CardAction>();
                if (logged)
                {
                    CardAction plButton = new CardAction()
                    {
                        Value = $@"{{ 'ActionType': '{BotActionTypes.AddBasket}', 'ProductId': '{item.Id}' , 'ProductName': '{item.Name}', 'PictureUrl': '{item.PictureUri}', 'UnitPrice': '{item.Price}'}}",
                        Type = "postBack",
                        Title = "Add to cart"
                    };
                    cardButtons.Add(plButton);
                }
                ThumbnailCard  plCard = new ThumbnailCard ()
                {
                    Title = item.Name,
                    Subtitle = $"**{item.Price} $**",
                    Text = $"{item.Description}",
                    Images = cardImages,
                    Buttons = cardButtons
                };

                attachments.Add(plCard.ToAttachment());
            }

            List<CardImage> moreImages = new List<CardImage>();
            // Todo add more image
            //moreImage.Add(new CardImage(url: item.PictureUri));

            List<CardAction> moreButtons = new List<CardAction>();
            CardAction moreButton = new CardAction()
            {
                Value = $@"{{ 'ActionType': '{BotActionTypes.NextPage}'}}",
                Type = "postBack",
                Title = "More"
            };
            moreButtons.Add(moreButton);
            ThumbnailCard moreCard = new ThumbnailCard()
            {
                Title = "More",
                Text = $"Show more items",
                Images = moreImages,
                Buttons = moreButtons
            };


            return attachments;
        }

        public async Task MessageReceivedAsync(IDialogContext context, IAwaitable<IMessageActivity> argument)
        {
            var message = await argument;
            
                if (message != null && message.Type == ActivityTypes.Message && !string.IsNullOrEmpty(message.Text))
                {
                    try
                    {
                        var json = JObject.Parse(message.Text);
                        var action = json.GetValue("ActionType");
                        switch (action.ToString())
                        {
                            case BotActionTypes.NextPage:
                                _currentPage++;
                                await ShowCatalog(context);
                                context.Wait(MessageReceivedAsync);
                                break;
                            case BotActionTypes.PreviousPage:
                                _currentPage--;
                                await ShowCatalog(context);
                                context.Wait(MessageReceivedAsync);
                                break;
                            case BotActionTypes.AddBasket:
                                await AskQuantity(context, json);
                                break;
                            case BotActionTypes.Login:
                                context.Call(new LoginDialog(), LoginReceivedAsync);
                                break;
                            case BotActionTypes.Back:
                                await context.PostAsync("Type what do you want to do.");
                                context.Done<object>(null);
                                break;
                        }
                    }
                    catch (JsonReaderException)
                    {
                        // is not a Json
                        await context.PostAsync("Please make a selection.");
                        await ShowCatalog(context);
                        context.Wait(MessageReceivedAsync);

                    }
                }
                else {
                    await context.PostAsync("Please make a selection.");
                    await ShowCatalog(context);
                    context.Wait(MessageReceivedAsync);
                }
           
        }

        private async Task AskQuantity(IDialogContext context, JObject json)
        {
            ItemToBuy = json;
            var producName = json.GetValue("ProductName").ToString();
            await context.PostAsync($"How many *{producName}* do you want to buy.");
            context.Wait(QuantityReceivedAsync);
        }

        private async Task QuantityReceivedAsync(IDialogContext context, IAwaitable<IMessageActivity> result)
        {
            var message = await result;
            int quantity = 1;
            if (Int32.TryParse(message.Text, out quantity))
            {
                await AddToBasket(context, ItemToBuy, quantity);
            }
            else
            {
                await context.PostAsync("Please type a number");
                context.Wait(QuantityReceivedAsync);
            }
        }

        private async Task LoginReceivedAsync(IDialogContext context, IAwaitable<bool> result)
        {
            await ShowCatalog(context);
            context.Wait(MessageReceivedAsync);
        }

        private async Task AddToBasket(IDialogContext context, JObject json, int quantity)
        {
            BotData userData = await context.GetUserDataAsync();
            AuthUser authUser = userData.GetProperty<AuthUser>("authUser");
            // TODO Check Expired
            if (authUser != null)
            {
                var reply = context.MakeMessage();
                var producName = json.GetValue("ProductName").ToString();
                var product = new BasketItem()
                {
                    Id = Guid.NewGuid().ToString(),
                    Quantity = quantity,
                    ProductName = producName,
                    PictureUrl = json.GetValue("PictureUrl").ToString(),
                    UnitPrice = json.GetValue("UnitPrice").ToObject<decimal>(),
                    ProductId = json.GetValue("ProductId").ToString(),
                };
                await serviceBasket.AddItemToBasket(authUser.UserId, product, authUser.AccessToken);
                reply.Text = $"You have added {producName} to your basket.";
                context.Call(new BasketDialog(), BasketAsync);
            }
        }

        private async Task BasketAsync(IDialogContext context, IAwaitable<object> result)
        {
            await ShowCatalog(context);
            context.Wait(MessageReceivedAsync);
        }
    }
}