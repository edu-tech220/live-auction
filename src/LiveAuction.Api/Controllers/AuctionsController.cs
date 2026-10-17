using LiveAuction.Api.Common;
using LiveAuction.Application.Auctions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiveAuction.Api.Controllers;

[Route("api/auctions")]
public sealed class AuctionsController(IAuctionService auctions) : ApiControllerBase
{
    // Consultar es público (catálogo); administrar requiere la política "Admin".

    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<PagedResult<AuctionSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] AuctionListQuery query, CancellationToken ct) =>
        Ok(await auctions.ListAsync(query, ct));

    [HttpGet("{id:int}", Name = nameof(GetAuction))]
    [AllowAnonymous]
    [ProducesResponseType<AuctionDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAuction(int id, CancellationToken ct)
    {
        var result = await auctions.GetAsync(id, ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);
    }

    [HttpPost]
    [Authorize(Policy = AuthPolicies.Admin)]
    [ProducesResponseType<AuctionDetailsDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(AuctionRequest request, CancellationToken ct)
    {
        var result = await auctions.CreateAsync(CurrentUserId, request, ct);
        return result.IsSuccess
            ? CreatedAtRoute(nameof(GetAuction), new { id = result.Value.Id }, result.Value)
            : Problem(result.Error!);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    [ProducesResponseType<AuctionDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(int id, AuctionRequest request, CancellationToken ct)
    {
        var result = await auctions.UpdateAsync(id, request, ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);
    }

    [HttpPost("{id:int}/cancel")]
    [Authorize(Policy = AuthPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        var result = await auctions.CancelAsync(id, ct);
        return result.IsSuccess ? NoContent() : Problem(result.Error!);
    }
}
