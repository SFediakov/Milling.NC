using Miller.Core.Analysis;
using Miller.Core.HeightMaps;

namespace Miller.Core.Simulation;

// What a collision entered in one stock cell: stock only, or the model as well (the tool surface lay
// below the model surface there). Model outranks Stock when a cell is entered several times.
public enum CollisionContact : byte
{
    None,
    Stock,
    Model,
}

// The collisions of one run over a toolpath. Contacts is indexed like the stock grid.
public sealed record CollisionReport(IReadOnlyList<SimulationEvent> Events, CollisionContact[] Contacts, int ModelSegments)
{
    public int Segments => Events.Select(e => e.SegmentIndex).Distinct().Count();

    public int Count(SimulationEventKind kind) => Events.Count(e => e.Kind == kind);

    public int Cells(CollisionContact contact) => Contacts.Count(c => c == contact);
}

// Collects collisions sample by sample: one event per segment and kind (the simulation panel and the
// generation summary share this rule), every entered stock cell, and the segments that entered the
// model. A cell has a model when the model stands above the floor there, as in FinalModelAnalyzer.
public sealed class CollisionRecorder
{
    private readonly ToolProfile _profile;
    private readonly float _cutterLength;
    private readonly HeightMap _model;
    private readonly float _floor;
    private readonly ContactHandler _onContact;
    private readonly List<SimulationEvent> _events = new();
    private readonly HashSet<(int Segment, SimulationEventKind Kind)> _reported = new();
    private readonly HashSet<int> _modelSegments = new();
    private bool _modelEntered;

    public CollisionRecorder(ToolProfile profile, float cutterLength, HeightMap model, float floor)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _cutterLength = cutterLength;
        _floor = floor;
        _onContact = OnContact;
        Contacts = new CollisionContact[model.CellCount];
    }

    public IReadOnlyList<SimulationEvent> Events => _events;

    public CollisionContact[] Contacts { get; }

    public int ModelSegments => _modelSegments.Count;

    // Checks one sample against the stock as it stands; returns the event when it is the first of its
    // segment and kind, null otherwise.
    public SimulationEvent? Record(HeightMap stock, SimulationSample sample)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (!stock.SameGridAs(_model))
        {
            throw new ArgumentException("Stock and model must share the same grid.", nameof(stock));
        }

        _modelEntered = false;
        var found = CollisionDetector.Check(stock, _profile, _cutterLength, sample.Tip, sample.Kind, sample.SegmentIndex, _onContact);
        if (found is null)
        {
            return null;
        }

        if (_modelEntered)
        {
            _modelSegments.Add(found.SegmentIndex);
        }

        if (!_reported.Add((found.SegmentIndex, found.Kind)))
        {
            return null;
        }

        _events.Add(found);
        return found;
    }

    public void Clear()
    {
        _events.Clear();
        _reported.Clear();
        _modelSegments.Clear();
        Array.Clear(Contacts);
    }

    public CollisionReport Report() => new(_events.ToArray(), (CollisionContact[])Contacts.Clone(), ModelSegments);

    private void OnContact(int i, int j, float surface)
    {
        var k = _model.Index(i, j);
        var m = _model.Z[k];
        var model = m > _floor + FinalModelAnalyzer.FloorTolerance && m > surface + CollisionDetector.Tolerance;
        if (model)
        {
            _modelEntered = true;
            Contacts[k] = CollisionContact.Model;
        }
        else if (Contacts[k] == CollisionContact.None)
        {
            Contacts[k] = CollisionContact.Stock;
        }
    }
}
