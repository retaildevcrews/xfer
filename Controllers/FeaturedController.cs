﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Imdb.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Ngsa.DataService.DataAccessLayer;
using Ngsa.Middleware;

namespace Ngsa.DataService.Controllers
{
    /// <summary>
    /// Handle /api/featured/movie requests
    /// </summary>
    [Route("api/[controller]")]
    public class FeaturedController : Controller
    {
        private static readonly NgsaLog Logger = new NgsaLog
        {
            Name = typeof(FeaturedController).FullName,
            ErrorMessage = "FeaturedControllerException",
            NotFoundError = "Movie Not Found",
        };

        private readonly IDAL dal;

        /// <summary>
        /// Initializes a new instance of the <see cref="FeaturedController"/> class.
        /// </summary>
        /// <param name="dal">data access layer instance</param>
        public FeaturedController()
        {
            dal = App.CosmosDal;
        }

        /// <summary>
        /// Returns a random movie from the featured movie list as a JSON Movie
        /// </summary>
        /// <response code="200">OK</response>
        /// <returns>IActionResult</returns>
        [HttpGet("movie")]
        public async Task<IActionResult> GetFeaturedMovieAsync()
        {
            IActionResult res;

            if (App.Config.AppType == AppType.WebAPI)
            {
                res = await DataService.Read<Movie>(Request).ConfigureAwait(false);
            }
            else
            {
                List<string> featuredMovies = await App.CacheDal.GetFeaturedMovieListAsync().ConfigureAwait(false);

                if (featuredMovies != null && featuredMovies.Count > 0)
                {
                    // get random featured movie by movieId
                    string movieId = featuredMovies[DateTime.UtcNow.Millisecond % featuredMovies.Count];

                    // get movie by movieId
                    res = await ResultHandler.Handle(dal.GetMovieAsync(movieId), Logger).ConfigureAwait(false);

                    // use cache dal on Cosmos 429 errors
                    if (App.Config.Cache && res is JsonResult jres && jres.StatusCode == 429)
                    {
                        Logger.LogWarning(nameof(GetFeaturedMovieAsync), "Served from cache", new LogEventId(429, "Cosmos 429 Result"), HttpContext);

                        res = await ResultHandler.Handle(App.CacheDal.GetMovieAsync(movieId), Logger).ConfigureAwait(false);
                    }
                }
                else
                {
                    return NotFound();
                }
            }

            return res;
        }
    }
}
