import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { useAuthStore } from '@/stores/authStore'
import MainLayout from '@/components/layout/MainLayout'
import Login from '@/pages/auth/Login'
import Register from '@/pages/auth/Register'
import NovelList from '@/pages/novel/NovelList'
import NovelDetailPage from '@/pages/novel/NovelDetail'
import NovelCreatePage from '@/pages/novel/NovelCreate'
import NovelEdit from '@/pages/novel/NovelEdit'
import CharacterList from '@/pages/character/CharacterList'
import CharacterDetailPage from '@/pages/character/CharacterDetail'
import CharacterCreatePage from '@/pages/character/CharacterCreate'
import ChapterList from '@/pages/chapter/ChapterList'
import ChapterDetailPage from '@/pages/chapter/ChapterDetail'
import ChapterCreatePage from '@/pages/chapter/ChapterCreate'
import ChapterEdit from '@/pages/chapter/ChapterEdit'
import ConsistencyCheck from '@/pages/consistency/ConsistencyCheck'
import ForeshadowingList from '@/pages/consistency/ForeshadowingList'
import StoryTracker from '@/pages/timeline/StoryTracker'
import EditorPage from '@/pages/editor/EditorPage'

function PrivateRoute({ children }: { children: React.ReactNode }) {
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated)
  return isAuthenticated ? <>{children}</> : <Navigate to="/login" replace />
}

function AppRoutes() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="/register" element={<Register />} />
        <Route
          path="/"
          element={
            <PrivateRoute>
              <MainLayout />
            </PrivateRoute>
          }
        >
          <Route index element={<Navigate to="/novels" replace />} />
          <Route path="novels" element={<NovelList />} />
          <Route path="novels/create" element={<NovelCreatePage />} />
          <Route path="novels/:id" element={<NovelDetailPage />} />
          <Route path="novels/:id/edit" element={<NovelEdit />} />
          <Route path="novels/:novelId/characters" element={<CharacterList />} />
          <Route path="novels/:novelId/characters/create" element={<CharacterCreatePage />} />
          <Route path="characters/:id" element={<CharacterDetailPage />} />
          <Route path="novels/:novelId/chapters" element={<ChapterList />} />
          <Route path="novels/:novelId/chapters/create" element={<ChapterCreatePage />} />
          <Route path="chapters/:id" element={<ChapterDetailPage />} />
          <Route path="chapters/:id/edit" element={<ChapterEdit />} />
          <Route path="novels/:novelId/consistency" element={<ConsistencyCheck />} />
          <Route path="novels/:novelId/foreshadowings" element={<ForeshadowingList />} />
          <Route path="novels/:novelId/tracker" element={<StoryTracker />} />
          <Route path="novels/:novelId/editor/:sessionId" element={<EditorPage />} />
          <Route path="novels/:novelId/editor" element={<EditorPage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}

export default AppRoutes
