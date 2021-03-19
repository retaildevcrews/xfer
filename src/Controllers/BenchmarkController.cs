﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Ngsa.Middleware;

namespace Ngsa.Application.Controllers
{
    /// <summary>
    /// Handle benchmark requests
    /// </summary>
    [Route("api/[controller]")]
    public class BenchmarkController : Controller
    {
        private static readonly NgsaLog Logger = new NgsaLog
        {
            Name = typeof(BenchmarkController).FullName,
            ErrorMessage = "BenchmarkControllerException",
        };

        /// <summary>
        /// Returns a JSON string array of Genre
        /// </summary>
        /// <param name="size">size of return</param>
        /// <response code="200">text/plain of size</response>
        /// <returns>IActionResult</returns>
        [HttpGet("{size}")]
        public async Task<IActionResult> GetDataAsync([FromRoute] int size)
        {
            IActionResult res;

            // validate size
            if (size < 1)
            {
                List<Middleware.Validation.ValidationError> list = new List<Middleware.Validation.ValidationError>
                {
                    new Middleware.Validation.ValidationError
                    {
                        Target = "size",
                        Message = "size must be > 0",
                    },
                };

                Logger.LogWarning(nameof(GetDataAsync), "Invalid Size", NgsaLog.LogEvent400, HttpContext);

                return ResultHandler.CreateResult(list, RequestLogger.GetPathAndQuerystring(Request));
            }

            if (size > 1000000)
            {
                List<Middleware.Validation.ValidationError> list = new List<Middleware.Validation.ValidationError>
                {
                    new Middleware.Validation.ValidationError
                    {
                        Target = "size",
                        Message = "size must be <= 1000000",
                    },
                };

                Logger.LogWarning(nameof(GetDataAsync), "Invalid Size", NgsaLog.LogEvent400, HttpContext);

                return ResultHandler.CreateResult(list, RequestLogger.GetPathAndQuerystring(Request));
            }

            if (App.Config.AppType == AppType.WebAPI)
            {
                res = await DataService.Read<string>(Request).ConfigureAwait(false);
            }
            else
            {
                int pageSize = size / 1000;
                pageSize = pageSize == 0 ? 1 : pageSize;

                // run a Cosmos query that simulates the size
                // each movie is approximately 1K
                _ = await App.Config.CosmosDal.GetMoviesAsync(new MovieQueryParameters { PageSize = pageSize });

                // return exact byte size
                res = await ResultHandler.Handle(App.Config.CacheDal.GetBenchmarkDataAsync(size), Logger).ConfigureAwait(false);
            }

            return res;
        }
    }
}