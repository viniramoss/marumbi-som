import { renderHook } from '@testing-library/react';
import { AutoScrollMaxStep, useDragAutoScroll } from './useDragAutoScroll';

const Type = 'application/x-marumbi-oferta';

/** jsdom não tem DragEvent: um Event com clientY e dataTransfer basta para o hook. */
function drag(name: string, clientY = 0, types: string[] = [Type]) {
  const event = new Event(name, { bubbles: true });
  Object.assign(event, { clientY, dataTransfer: { types } });
  document.body.dispatchEvent(event);
}

describe('useDragAutoScroll', () => {
  let frames: FrameRequestCallback[];
  const scrollBy = vi.fn();

  const runFrames = (count: number) => {
    for (let i = 0; i < count; i++) frames.shift()?.(0);
  };

  beforeEach(() => {
    frames = [];
    scrollBy.mockReset();
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => frames.push(cb));
    vi.stubGlobal('cancelAnimationFrame', () => (frames = []));
    vi.stubGlobal('scrollBy', scrollBy);
    vi.stubGlobal('innerHeight', 800);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('rola para cima com a oferta perto do topo e continua com o cursor parado', () => {
    renderHook(() => useDragAutoScroll(Type));

    drag('dragover', 0);
    runFrames(3);

    expect(scrollBy).toHaveBeenCalledTimes(3);
    expect(scrollBy).toHaveBeenLastCalledWith(0, -AutoScrollMaxStep);
  });

  it('rola para baixo perto da borda de baixo, mais devagar longe dela, e para ao soltar', () => {
    renderHook(() => useDragAutoScroll(Type));

    drag('dragover', 790);
    runFrames(1);
    const fast = scrollBy.mock.lastCall![1] as number;
    drag('dragover', 720);
    runFrames(1);
    const slow = scrollBy.mock.lastCall![1] as number;
    expect(fast).toBeGreaterThan(slow);
    expect(slow).toBeGreaterThan(0);

    drag('drop');
    runFrames(5);
    expect(scrollBy).toHaveBeenCalledTimes(2);
  });

  it('não rola no meio da tela nem com outro tipo de arraste', () => {
    renderHook(() => useDragAutoScroll(Type));

    drag('dragover', 400);
    runFrames(3);
    drag('dragend');
    drag('dragover', 0, ['Files']);
    runFrames(3);

    expect(scrollBy).not.toHaveBeenCalled();
  });
});
