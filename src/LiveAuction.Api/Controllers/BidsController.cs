using LiveAuction.Api.Common;
using LiveAuction.Application.Auctions;
using LiveAuction.Application.Bidding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiveAuction.Api.Controllers;

[Route("api/auctions/{auctionId:int}/bids")]
public sealed class BidsController(IBidService bids, BidThrottle throttle) : ApiControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<PagedResult<BidDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(int auctionId, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await bids.GetBidsAsync(auctionId, page, pageSize, ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);
    }

    // Alternativa REST al método PlaceBid del Hub (misma regla, mismo servicio, mismo evento).
    [HttpPost]
    [Authorize]
    [ProducesResponseType<PlaceBidResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Place(int auctionId, PlaceBidRequest request, CancellationToken ct)
    {
        if (!throttle.TryAcquire(CurrentUserId))
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Demasiadas pujas",
                detail: "Estás pujando demasiado rápido. Espera unos segundos.");

        var result = await bids.PlaceBidAsync(auctionId, CurrentUserId, request.Amount!.Value, ct);
        return result.IsSuccess
            ? Created($"/api/auctions/{auctionId}/bids/{result.Value.Bid.Id}", result.Value)
            : Problem(result.Error!);
    }
}

[Route("api/me")]
[Authorize]
public sealed class MeController(IBidService bids) : ApiControllerBase
{
    // "Mis pujas": el usuario sale del token; no existe /api/users/{id}/bids que permita espiar.
    [HttpGet("bids")]
    [ProducesResponseType<IReadOnlyList<MyBidDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyBids(CancellationToken ct) => Ok(await bids.GetMyBidsAsync(CurrentUserId, ct));
}
