﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CacheCow.Common;
using CacheCow.Common.Helpers;
using CacheCow.Server.Core;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;
using System.IO;

namespace CacheCow.Server.Core.Mvc
{
    /// <summary>
    /// A resource filter responsibility for implementing HTTP Caching
    /// Use it with HttpCacheFactoryAttribute
    /// </summary>
    public class HttpCacheFilter : IAsyncResourceFilter
    {
        private ICacheabilityValidator _validator;
        private readonly ITimedETagExtractor _timedETagExtractor;
        private readonly ITimedETagQueryProvider _timedETagQueryProvider;

        private const string StreamName = "##__travesty_that_I_have_to_do_this__##";

        public HttpCacheFilter(ICacheabilityValidator validator,
            ICacheDirectiveProvider cacheDirectiveProvider,
            ITimedETagExtractor timedETagExtractor,
            ITimedETagQueryProvider timedETagQueryProvider)
        {
            _validator = validator;
            CacheDirectiveProvider = cacheDirectiveProvider;
            _timedETagExtractor = timedETagExtractor;
            _timedETagQueryProvider = timedETagQueryProvider;
            ApplyNoCacheNoStoreForNonCacheableResponse = true;
        }

        /// <summary>
        /// Happens at the incoming (executING)
        /// </summary>
        /// <param name="timedEtag"></param>
        /// <param name="cacheValidationStatus"></param>
        /// <param name="context">
        /// </param>
        /// <returns>
        /// True: applied and the call can exit 
        /// False: tried to apply but did not match hence the call should continue
        /// null: could not apply (timedEtag was null)
        /// </returns>
        protected bool? ApplyCacheValidation(TimedEntityTagHeaderValue timedEtag,
            CacheValidationStatus cacheValidationStatus,
            ResourceExecutingContext context)
        {
            if (timedEtag == null)
                return null;

            var headers = context.HttpContext.Request.GetTypedHeadersWithCaching();
            switch (cacheValidationStatus)
            {
                case CacheValidationStatus.GetIfModifiedSince:
                    if (timedEtag.LastModified == null)
                        return false;
                    else
                    {
                        if (timedEtag.LastModified > headers.IfModifiedSince.Value)
                            return false;
                        else
                        {
                            context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
                            return true;
                        }
                    }

                case CacheValidationStatus.GetIfNoneMatch:
                    if (timedEtag.ETag == null)
                        return false;
                    else
                    {
                        if (headers.IfNoneMatch.Any(x => x.Tag == timedEtag.ETag.Tag))
                        {
                            context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
                            return true;
                        }
                        else
                            return false;
                    }
                case CacheValidationStatus.PutIfMatch:
                    if (timedEtag.ETag == null)
                        return false;
                    else
                    {
                        if (headers.IfMatch.Any(x => x.Tag == timedEtag.ETag.Tag))
                            return false;
                        else
                        {
                            context.Result = new StatusCodeResult(StatusCodes.Status409Conflict);
                            return true;
                        }
                    }
                case CacheValidationStatus.PutIfUnModifiedSince:
                    if (timedEtag.LastModified == null)
                        return false;
                    else
                    {
                        if (timedEtag.LastModified > headers.IfUnmodifiedSince.Value)
                        {
                            context.Result = new StatusCodeResult(StatusCodes.Status409Conflict);
                            return true;
                        }
                        else
                            return false;
                    }

                default:
                    return null;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <param name="next"></param>
        /// <returns></returns>
        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            bool? cacheValidated = null;
            bool isRequestCacheable = _validator.IsCacheable(context.HttpContext.Request);
            var cacheValidationStatus = context.HttpContext.Request.GetCacheValidationStatus();
            if (cacheValidationStatus != CacheValidationStatus.None)
            {
                var timedETag = await _timedETagQueryProvider.QueryAsync(context);
                cacheValidated = ApplyCacheValidation(timedETag, cacheValidationStatus, context);
                if (cacheValidated ?? false)
                {
                    // the response would have been set and no need to run the rest of the pipeline
                    return;
                }
            }

            context.HttpContext.Items[StreamName] = context.HttpContext.Response.Body;
            context.HttpContext.Response.Body = new MemoryStream();

            var execCtx = await next(); // _______________________________________________________________________________

            var ms = context.HttpContext.Response.Body as MemoryStream;
            bool mustReflush = ms != null && ms.Length > 0;
            context.HttpContext.Response.Body = (Stream) context.HttpContext.Items[StreamName];

            try
            {
                if (HttpMethods.IsGet(context.HttpContext.Request.Method))
                {
                    var or = execCtx.Result as ObjectResult;
                    TimedEntityTagHeaderValue tet = null;
                    if (or != null && or.Value != null)
                    {
                        tet = _timedETagExtractor.Extract(or.Value);
                    }

                    if (cacheValidated == null  // could not validate
                        && tet != null
                        && cacheValidationStatus != CacheValidationStatus.None) // can only do GET validation, PUT is already impacted backend stores
                    {
                        cacheValidated = ApplyCacheValidation(tet, cacheValidationStatus, context);
                        if (cacheValidated ?? false)
                            return;
                    }

                    if (tet != null)
                        context.HttpContext.Response.ApplyTimedETag(tet);

                    var isResponseCacheable = _validator.IsCacheable(context.HttpContext.Response);
                    if (!isRequestCacheable || !isResponseCacheable)
                    {
                        if (!execCtx.Canceled)
                            context.HttpContext.Response.MakeNonCacheable();
                    }

                    if (isResponseCacheable)
                    {
                        context.HttpContext.Response.Headers[HttpHeaderNames.CacheControl] =
                            CacheDirectiveProvider.Get(context.HttpContext).ToString();
                    }
                }

            }
            finally
            {
                if (mustReflush)
                {
                    ms.CopyTo(context.HttpContext.Response.Body);
                }
            }
        }

        /// <summary>
        /// Whether in addition to sending cache directive for cacheable resources, it should send such directives for non-cachable resources
        /// </summary>
        public bool ApplyNoCacheNoStoreForNonCacheableResponse { get; set; }

        public ICacheDirectiveProvider CacheDirectiveProvider { get; set; }
    }

    /// <summary>
    /// Generic variant of HttpCacheFilter
    /// </summary>
    /// <typeparam name="T">View Model Type</typeparam>
    public class HttpCacheFilter<T> : HttpCacheFilter
    {
        public HttpCacheFilter(ICacheabilityValidator validator,
            ICacheDirectiveProvider<T> cacheDirectiveProvider,
            ITimedETagExtractor<T> timedETagExtractor,
            ITimedETagQueryProvider<T> timedETagQueryProvider) :
            base(validator, cacheDirectiveProvider, timedETagExtractor, timedETagQueryProvider)
        {
        }
    }
}