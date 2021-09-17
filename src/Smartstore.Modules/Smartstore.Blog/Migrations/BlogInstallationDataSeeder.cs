﻿using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Smartstore.Blog.Domain;
using Smartstore.Blog.Services;
using Smartstore.Core.Data;
using Smartstore.Core.Data.Migrations;
using Smartstore.Core.Messaging;
using Smartstore.Core.Seo;
using Smartstore.Engine.Modularity;
using Smartstore.IO;

namespace Smartstore.Blog.Migrations
{
    internal class BlogInstallationDataSeeder : DataSeeder<SmartDbContext>
    {
        private readonly ModuleInstallationContext _installContext;
        private readonly IMessageTemplateService _messageTemplateService;

        public BlogInstallationDataSeeder(ModuleInstallationContext installContext, IMessageTemplateService messageTemplateService)
            : base(installContext.ApplicationContext, installContext.Logger)
        {
            _installContext = Guard.NotNull(installContext, nameof(installContext));
            _messageTemplateService = Guard.NotNull(messageTemplateService, nameof(messageTemplateService));
        }

        protected override async Task SeedCoreAsync()
        {
            await PopulateAsync("PopulateBlogMessageTemplates", PopulateMessageTemplates);

            if (_installContext.SeedSampleData == null || _installContext.SeedSampleData == true)
            {
                await PopulateAsync("PopulateBlogPosts", PopulateBlogPosts);
            }
        }

        private async Task PopulateMessageTemplates()
        {
            await _messageTemplateService.ImportAllTemplatesAsync(
                _installContext.Culture, 
                PathUtility.Combine(_installContext.ModuleDescriptor.Path, "App_Data/EmailTemplates"));
        }

        private async Task PopulateBlogPosts()
        {
            if (await Context.Set<BlogPost>().AnyAsync())
            {
                return;
            }

            var converter = new BlogPostConverter(Context, _installContext);
            var blogPosts = await converter.ImportAllAsync();
            await PopulateUrlRecordsFor(blogPosts, post => CreateUrlRecordFor(post));
        }

        public UrlRecord CreateUrlRecordFor(BlogPost post)
        {
            var name = BuildSlug(post.GetDisplayName()).Truncate(400);

            if (name.HasValue())
            {
                var result = new UrlRecord
                {
                    EntityId = post.Id,
                    EntityName = post.GetEntityName(),
                    LanguageId = 0,
                    Slug = name,
                    IsActive = true
                };

                return result;
            }

            return null;
        }
    }
}
